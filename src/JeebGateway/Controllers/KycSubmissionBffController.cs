using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// S03 thin KYC BFF for the JSON submit + ToS-sign flow (DEC2 / DEC3). The
/// gateway is the ONLY aggregator: it composes the owning <c>kyc-service</c> (via
/// <see cref="IKycBffSeam"/>) with the live <c>contract-signing-service</c>, and
/// holds ZERO KYC business state itself (ADR-0004 / ARCH LAW). kyc-service owns
/// the submission aggregate, the SM-6 state machine, the ToS-acceptance record,
/// and Idempotency-Key dedup; this controller orchestrates and projects.
///
/// Endpoints:
/// <list type="bullet">
///   <item><c>POST /v1/kyc/contract-template/sign</c> (DEC3) — record the ToS
///     signature in contract-signing, THEN stamp <c>tos_signed_at</c> in the KYC
///     domain. Idempotent; never echoes the raw signature blob.</item>
///   <item><c>POST /v1/kyc/submit</c> + <c>POST /kyc/submit</c> (JSON alias, DEC2)
///     — submit the assembled package BY REFERENCE (object_refs from the cdn
///     signed-PUT broker), → 201 <c>Submitted</c>; idempotent replay → 200 same
///     id (N9).</item>
/// </list>
///
/// RFC 7807 ProblemDetails throughout. Idempotency-Key on both writes (N9/N10).
/// </summary>
[ApiController]
// ADR-005 §L: KYC submission self-service (sign ToS, submit package, read own status) is one
// capability — kyc.submit.self {client, jeeber}. A client upgrading to jeeber holds {client} at
// submission time and is authorized. Declared class-level (all three actions share the cap).
// Per-user ownership, SM-6 legality, and Idempotency-Key dedup stay STATE in kyc-service.
[RequireCapability(Capabilities.KycSubmitSelf)]
public sealed class KycSubmissionBffController : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    // JEBV4-8 — when FeatureFlags:Kyc:AutoApprove is ON (default ON only on MSI
    // dev/staging via appsettings.Production.json) a fresh KYC submission is
    // adjudicated Verified + role-granted inline with NO admin step and NO re-login.
    private const string AutoApproveFlagKey = "FeatureFlags:Kyc:AutoApprove";
    private const string AutoApproveReviewerId = "system:kyc-auto-approve";

    private readonly IKycBffSeam _kyc;
    private readonly IContractSigningServiceClient _contractSigning;
    private readonly IUserManagementDualRoleClient _userManagement;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IConfiguration _config;
    private readonly ILogger<KycSubmissionBffController> _log;

    public KycSubmissionBffController(
        IKycBffSeam kyc,
        IContractSigningServiceClient contractSigning,
        IUserManagementDualRoleClient userManagement,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IConfiguration config,
        ILogger<KycSubmissionBffController> log)
    {
        _kyc = kyc;
        _contractSigning = contractSigning;
        _userManagement = userManagement;
        _flags = flags;
        _config = config;
        _log = log;
    }

    /// <summary>
    /// S03 H5 / N1 / N10 (DEC3). Records the caller's ToS signature, then stamps
    /// the KYC ToS-acceptance. The signature ceremony is the generic
    /// contract-signing primitive; the <c>tos_signed_at</c> stamp is the KYC
    /// domain's. Idempotent on Idempotency-Key (replay returns the original
    /// stamp, no double-sign). The raw blob is never persisted in the KYC domain
    /// and never echoed back.
    /// </summary>
    [HttpPost("v1/kyc/contract-template/sign")]
    [ProducesResponseType(typeof(KycTosSignResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SignTos(
        [FromBody] KycTosSignBody? body,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.SignatureBlob))
        {
            // N1 (E1): an empty signature blob is invalid (JEB-41 AC3).
            return InvalidSignature("signature_blob is required and must be a non-empty base64 signature.");
        }

        if (string.IsNullOrWhiteSpace(body.TosVersion))
        {
            return Problem(
                type: "https://jeeb.dev/errors/invalid-request",
                title: "Invalid sign request",
                detail: "tos_version is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var idempotencyKey = ResolveIdempotencyKey();

        // 1) Record the signature in the contract-signing primitive (when live).
        //    The proof ref is the verifiable handle the KYC domain stores — NOT
        //    the raw blob. When contract-signing is not yet deployed (flag off),
        //    derive a deterministic gateway-side proof ref so the ToS-acceptance
        //    record still references a stable, non-PII handle.
        //
        //    CEREMONY (the contract-signing primitive is template → contract →
        //    signature, NOT a one-shot sign-by-template). The Jeeb ToS template
        //    declares a single accepting party with role_key="client" (see the
        //    seeded jeeb_tos_v1). So we:
        //      a) CreateContract(template_id, parties:[{role_key:"client",
        //         party_ref:userId}], actor:{type:"PARTY", ref:userId}) → contractId
        //      b) Sign(contractId, role_key:"client", party_ref:userId, proofRef)
        //    Signing a *template id* as if it were a *contract id* (the old bug)
        //    yielded an upstream 404 CONTRACT_NOT_FOUND → gateway 502.
        string? signatureProofRef;
        var derivedProofRef = DeriveProofRef(body.SignatureBlob!);
        // The contract-signing signature's signed_at is the AUTHORITATIVE ToS
        // acceptance timestamp (contract-signing is the source of truth for the
        // signature). The KYC stamp below mirrors it onto the submission when one
        // exists; when the ToS is signed BEFORE the package is submitted (H5 runs
        // before H6 by design) there is no submission to stamp, so this is the
        // timestamp returned to the caller.
        DateTimeOffset? ceremonySignedAt = null;
        if (_flags.CurrentValue.ContractSigning && !string.IsNullOrWhiteSpace(body.TemplateId))
        {
            try
            {
                var contract = await _contractSigning.CreateContractAsync(
                    new CreateContractRequest
                    {
                        TemplateId = body.TemplateId!,
                        Parties = new List<ContractParty>
                        {
                            new() { RoleKey = "client", PartyRef = userId },
                        },
                        Actor = new ActorInfo
                        {
                            Type = "PARTY",
                            Ref = userId,
                            Reason = "jeeb_tos_acceptance",
                        },
                    },
                    ct);

                if (string.IsNullOrWhiteSpace(contract.ContractId))
                {
                    _log.LogWarning("contract-signing CreateContract returned no contract_id for user {UserId}", userId);
                    return Problem(
                        type: "https://jeeb.dev/errors/upstream-unavailable",
                        title: "Signature ceremony failed",
                        detail: "The contract-signing upstream did not return a contract id.",
                        statusCode: StatusCodes.Status502BadGateway);
                }

                var signature = await _contractSigning.SignAsync(
                    contract.ContractId!,
                    new SignRequest
                    {
                        RoleKey = "client",
                        PartyRef = userId,
                        SignatureProofRef = derivedProofRef,
                    },
                    ct);
                signatureProofRef = signature.SignatureProofRef ?? derivedProofRef;
                ceremonySignedAt = signature.SignedAt;
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex, "contract-signing ceremony failed for user {UserId}", userId);
                return Problem(
                    type: "https://jeeb.dev/errors/upstream-unavailable",
                    title: "Signature ceremony failed",
                    detail: "The contract-signing upstream rejected the signature.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }
        else
        {
            signatureProofRef = derivedProofRef;
        }

        // 2) Mirror the ToS-acceptance onto the owning kyc-service WHEN the user
        //    already has a submission to stamp. The ToS is signed BEFORE the package
        //    is assembled (H5 precedes H6), so most first-time signs have NO
        //    submission yet — stamping a non-existent submission id makes
        //    kyc-service fault. In that case the contract-signing signature is the
        //    record of truth and we return its signed_at. This is NON-fail-closed:
        //    a stamp fault never sinks the ceremony (the signature is already
        //    durable in contract-signing); the later submit re-attaches the
        //    acceptance via tos_accepted_version.
        DateTimeOffset tosSignedAt;
        try
        {
            var stamp = await _kyc.StampTosAsync(userId, body.TosVersion!, signatureProofRef, idempotencyKey, ct);
            tosSignedAt = stamp.TosSignedAt;
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }
        catch (Exception ex) when (ceremonySignedAt is not null)
        {
            // No submission to stamp yet (the common pre-submit path) — fall back to
            // the authoritative contract-signing signed_at. The acceptance is NOT
            // lost: it lives in contract-signing and is re-linked on submit.
            _log.LogInformation(ex,
                "kyc tos-stamp skipped (no submission yet) for user {UserId}; using contract-signing signed_at", userId);
            tosSignedAt = ceremonySignedAt.Value;
        }

        return Ok(new KycTosSignResponse
        {
            TosSignedAt = tosSignedAt,
            TosAcceptedVersion = body.TosVersion!,
        });
    }

    /// <summary>
    /// S03 H6 / A3 / N9 (DEC2). Submits the assembled KYC package BY REFERENCE.
    /// The four document refs are object_refs the client already PUT directly to
    /// cdn-service via the signed-PUT broker (H2/H2b) — no bytes flow through the
    /// gateway here. Returns 201 with <c>state: "Submitted"</c>; an idempotent
    /// replay returns 200 with the SAME submissionId and no new row (N9).
    /// </summary>
    [HttpPost("v1/kyc/submit")]
    [HttpPost("kyc/submit")]
    [HttpPost("kyc/submit/json")]
    [Consumes("application/json")]
    [ProducesResponseType(typeof(KycSubmitJsonResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(KycSubmitJsonResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SubmitJson(
        [FromBody] KycSubmitJsonBody? body,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return Problem(
                type: "https://jeeb.dev/errors/invalid-request",
                title: "Invalid submission",
                detail: "request body is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Minimal, contract-true validation of the document refs the package needs.
        // The full field-set validation against the form-builder render schema is
        // the domain's; here the BFF enforces the required refs are present.
        // E3 (owner decision Q-039): vehicle information is REMOVED from the KYC
        // contract, so vehicle_registration_url is NO LONGER a hard-required ref
        // (it is still forwarded to kyc-service when a caller supplies it). The
        // required refs are the two id-document faces and the liveness selfie.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(body.IdDocumentFrontUrl)) missing.Add("id_document_front_url");
        if (string.IsNullOrWhiteSpace(body.IdDocumentBackUrl)) missing.Add("id_document_back_url");
        if (string.IsNullOrWhiteSpace(body.SelfieWithLivenessUrl)) missing.Add("selfie_with_liveness_url");
        if (missing.Count > 0)
        {
            return Problem(
                type: "https://jeeb.dev/errors/invalid-request",
                title: "Missing document references",
                detail: $"the following document refs are required: {string.Join(", ", missing)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // S03 N2 / N3 — field-level validation (JEB-40 AC6 / AC8). These are the
        // gateway's input-contract checks; the domain still re-validates. We return
        // a field-scoped RFC 7807 ProblemDetails (which field + which rule) BEFORE
        // touching the owning kyc-service.
        var fieldError = await ValidateSubmitFieldsAsync(body, ct);
        if (fieldError is not null) return fieldError;

        var idempotencyKey = ResolveIdempotencyKey();

        KycBffSubmitResult result;
        try
        {
            result = await _kyc.SubmitByRefAsync(new KycBffSubmitInput
            {
                UserId = userId,
                IdType = body.IdType,
                IdNumber = body.IdNumber,
                IdDocumentFrontRef = body.IdDocumentFrontUrl,
                IdDocumentBackRef = body.IdDocumentBackUrl,
                DriverLicenseNumber = body.DriverLicenseNumber,
                DriverLicenseExpiry = body.DriverLicenseExpiry,
                VehicleRegistrationRef = body.VehicleRegistrationUrl,
                VehiclePlateNumber = body.VehiclePlateNumber,
                VehicleYearMakeModel = body.VehicleYearMakeModel,
                SelfieWithLivenessRef = body.SelfieWithLivenessUrl,
                TosAcceptedVersion = body.TosAcceptedVersion,
            }, idempotencyKey, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        // JEBV4-8 — AUTO-APPROVE (kills the admin blocker). When
        // FeatureFlags:Kyc:AutoApprove is ON, a first-time submission is
        // adjudicated Verified via the SAME kyc-service review seam the admin path
        // uses, the jeeber role is granted in user-management (kyc-service never
        // calls UM — ADR-0004), and the submit RESPONSE returns state="Verified" so
        // the app renders 'approved' (and the JeeberRoleActivator fires) with NO
        // re-login. Best-effort: the submit already committed, so an approve/grant
        // blip is logged and never rolls the submit back (mirrors AdminKyc N14). A
        // replay (200) is skipped — the original submit already auto-approved.
        var state = result.State;
        if (!result.Replayed && _config.GetValue<bool>(AutoApproveFlagKey))
        {
            var verifiedState = await TryAutoApproveAsync(result.SubmissionId, ct);
            if (!string.IsNullOrWhiteSpace(verifiedState))
            {
                state = verifiedState!;
            }
        }

        var payload = new KycSubmitJsonResponse
        {
            SubmissionId = result.SubmissionId,
            State = state,
            IdType = body.IdType,
            IdDocumentFrontUrl = body.IdDocumentFrontUrl,
            IdDocumentBackUrl = body.IdDocumentBackUrl,
            VehicleRegistrationUrl = body.VehicleRegistrationUrl,
            SelfieWithLivenessUrl = body.SelfieWithLivenessUrl,
            TosSignedAt = result.TosSignedAt,
            TosAcceptedVersion = result.TosAcceptedVersion,
        };

        // N9: replay returns 200 with the same id; first submit returns 201.
        return result.Replayed
            ? Ok(payload)
            : StatusCode(StatusCodes.Status201Created, payload);
    }

    /// <summary>
    /// JEBV4-8 auto-approve. Adjudicates the just-submitted package via the SAME
    /// kyc-service review seam the admin path uses (Approve → Verified), then
    /// composes the jeeber role grant in user-management. Returns the new upstream
    /// status ("Verified") on success, or null when the flow could not complete —
    /// in which case the caller keeps the submitted state and the submit is NEVER
    /// rolled back (an admin can still approve manually). Fully best-effort: every
    /// upstream fault (503 disabled, 409 conflict, invalid-role, UM blip) is
    /// swallowed and logged so a fresh submit can never hard-fail on auto-approve.
    /// </summary>
    private async Task<string?> TryAutoApproveAsync(string submissionId, CancellationToken ct)
    {
        try
        {
            var outcome = await _kyc.ReviewAsync(submissionId, new KycBffReviewInput
            {
                Action = KycReviewAction.Approve,
                ReviewerId = AutoApproveReviewerId,
                Reason = "auto-approved (FeatureFlags:Kyc:AutoApprove)",
            }, ct);

            // CP-C / H8 role grant — kyc-service decides the grant INTENT
            // (GrantsRole = "jeeber"); the GATEWAY composes the UM append.
            if (!string.IsNullOrWhiteSpace(outcome.GrantsRole))
            {
                await ComposeRoleGrantAsync(outcome.UserId, outcome.GrantsRole!, submissionId, ct);
            }

            _log.LogInformation(
                "kyc auto-approve ok: submission {SubmissionId} → {Status}", submissionId, outcome.Status);
            return outcome.Status;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "kyc auto-approve failed for submission {SubmissionId}; submit committed, approval left to admin",
                submissionId);
            return null;
        }
    }

    /// <summary>
    /// Composes the user-management jeeber-role append for the auto-approve outcome,
    /// mirroring <c>AdminKycController.ComposeRoleGrantAsync</c>: translate the Jeeb
    /// contract role → opaque (jeeber → driver) and append to available_roles
    /// (set-semantics). kyc-service never calls UM (ARCH LAW). Non-fatal: an unknown
    /// role or a UM blip is logged; the approve already committed.
    /// </summary>
    private async Task ComposeRoleGrantAsync(
        string? subjectUserId, string contractRole, string submissionId, CancellationToken ct)
    {
        var opaqueRole = JeebRoleTranslator.ToOpaque(contractRole);
        if (opaqueRole is null)
        {
            _log.LogWarning(
                "kyc auto-approve {SubmissionId}: grant role '{Role}' is not a Jeeb contract role; grant skipped",
                submissionId, contractRole);
            return;
        }

        // Interim path (UM upstream off): the seam already granted the role locally.
        if (!_flags.CurrentValue.UserManagement)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(subjectUserId))
        {
            _log.LogWarning(
                "kyc auto-approve {SubmissionId}: review outcome carried no owner; role grant skipped", submissionId);
            return;
        }

        try
        {
            var grant = await _userManagement.AppendAvailableRoleAsync(subjectUserId, opaqueRole, ct);
            _log.LogInformation(
                "kyc auto-approve {SubmissionId}: granted opaque role '{Role}' (added={Added})",
                submissionId, opaqueRole, grant.Added);
        }
        catch (UserManagementCallException ex)
        {
            _log.LogWarning(ex,
                "kyc auto-approve {SubmissionId}: user-management role append failed (status {Status}); "
                + "approve committed, role grant deferred", submissionId, ex.StatusCode);
        }
    }

    /// <summary>
    /// S03 H7 / N7. Reads the caller's latest KYC submission status, projected
    /// from the owning kyc-service. 404 when the user has no submission yet (N7).
    /// </summary>
    [HttpGet("v1/kyc/status")]
    [HttpGet("kyc/status")]
    [ProducesResponseType(typeof(KycStatusBffResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Status(CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        KycBffSubmissionView? view;
        try
        {
            view = await _kyc.GetLatestForUserAsync(userId, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        if (view is null)
        {
            // N7: no submission for this user.
            return NotFound(new ProblemDetails
            {
                Type = "https://jeeb.dev/errors/not-found",
                Title = "No KYC submission",
                Detail = "No KYC submission exists for the current user.",
                Status = StatusCodes.Status404NotFound,
            });
        }

        return Ok(new KycStatusBffResponse
        {
            SubmissionId = view.SubmissionId,
            State = view.Status,
            RejectionReason = view.RejectionReason,
            TosSignedAt = view.TosSignedAt,
            TosAcceptedVersion = view.TosAcceptedVersion,
            SubmittedAt = view.SubmittedAt,
            ReviewedAt = view.ReviewedAt,
        });
    }

    // --- helpers -----------------------------------------------------------

    private IActionResult KycUpstreamDisabled() => Problem(
        type: "https://jeeb.dev/errors/upstream-unavailable",
        title: "KYC upstream unavailable",
        detail: "The KYC service is not enabled.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    // S03 N2 / N3 input-contract validation (JEB-40 AC6 / AC8). Returns a
    // field-scoped 400 ProblemDetails on the first violation, or null when valid.
    private static readonly System.Text.RegularExpressions.Regex NationalIdRegex =
        new(@"^\d{12}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Accepted ID variants per owner decision Q-042 / the E3 DoD
    // (WORK-ORDER-2026-07-07 Lane E, E3): the BFF enumerates EXACTLY
    // national_id | passport | residency — "no more" — and any other value is
    // rejected. No back-compat alias is kept: no client ever sent
    // "residency_permit" (repo-wide search, form-builder jeeb_jeeber_v1 flavors,
    // and mobile PR #80 all corroborate).
    private static readonly HashSet<string> AllowedIdTypes =
        new(StringComparer.OrdinalIgnoreCase) { "national_id", "passport", "residency" };

    private async Task<IActionResult?> ValidateSubmitFieldsAsync(KycSubmitJsonBody body, CancellationToken ct)
    {
        // E3 (owner decision Q-039 — "Id number is a must"): id_type is REQUIRED.
        // The JEBV4-113 lane found the BFF treated id_type as optional; E3 makes
        // both id_type and id_number mandatory collected fields at the BFF.
        if (string.IsNullOrWhiteSpace(body.IdType))
        {
            return FieldProblem("id_type", "id_type is required.");
        }

        // AC6 — id_type must be one of the supported enum values.
        if (!AllowedIdTypes.Contains(body.IdType!))
        {
            return FieldProblem("id_type", $"id_type '{body.IdType}' is not a supported value (expected one of: {string.Join(", ", AllowedIdTypes)}).");
        }

        // E3 (Q-039) — id_number is a must for EVERY id_type, so it must be
        // present. This supersedes the pre-E3 JEBV4-113 §3.1 scoping that only
        // required id_number for national_id.
        if (string.IsNullOrWhiteSpace(body.IdNumber))
        {
            return FieldProblem("id_number", "id_number is required.");
        }

        // AC6 — the national-ID 12-digit shape rule (^\d{12}$) stays SCOPED to
        // id_type == national_id (JEBV4-113 §3.1): passport / residency numbers
        // carry a different shape and must not be blocked by a national-ID rule.
        if (string.Equals(body.IdType, "national_id", StringComparison.OrdinalIgnoreCase)
            && !NationalIdRegex.IsMatch(body.IdNumber!))
        {
            return FieldProblem("id_number", "id_number must be exactly 12 digits (^\\d{12}$).");
        }

        // AC8 — tos_accepted_version must cross-link to a known ToS template
        // version (no dangling/latent contract reference, BR-3). Resolved against
        // the live contract-signing catalog so the rule is data-true, not a
        // hardcoded literal. When contract-signing is unavailable the BFF does NOT
        // fail closed on this optional cross-link (it lets the domain decide).
        if (!string.IsNullOrWhiteSpace(body.TosAcceptedVersion) && _flags.CurrentValue.ContractSigning)
        {
            string? knownVersion = null;
            try
            {
                var catalog = await _contractSigning.ListTemplatesAsync(ct);
                knownVersion = ResolveKnownTosVersion(catalog);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex, "contract-signing catalog read failed during ToS cross-link validation");
                // Do not fail closed on a transient catalog read — defer to the domain.
            }

            if (knownVersion is not null
                && !string.Equals(body.TosAcceptedVersion, knownVersion, StringComparison.OrdinalIgnoreCase))
            {
                return FieldProblem(
                    "tos_accepted_version",
                    $"tos_accepted_version '{body.TosAcceptedVersion}' does not resolve to a known ToS template version.");
            }
        }

        return null;
    }

    // Resolves the version marker ("v1") of the Jeeb client ToS template from the
    // contract-signing catalog, by name. Mirrors the H4 resolution (KycBffController)
    // so submit and template-fetch agree on the same source of truth. The canonical
    // template NAME is jeeb_tos_v1 (KycBffController.JeebTosTemplateName); the old
    // hyphenated literal here matched no real catalog item, so the AC8 cross-check
    // silently never fired (JEBV4-197 bonus fix).
    private const string JeebTosTemplateName = "jeeb_tos_v1";

    private static string? ResolveKnownTosVersion(JsonElement catalog)
    {
        if (catalog.ValueKind != JsonValueKind.Object
            || !catalog.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : null;
            if (string.Equals(name, JeebTosTemplateName, StringComparison.OrdinalIgnoreCase))
            {
                // Trailing "_vN" segment is the stable version marker (jeeb_tos_v1
                // -> "v1"), mirroring KycBffController.TemplateVersionFor so the
                // cross-link compares against the SAME value the mobile client is
                // handed by GET /v1/kyc/contract-template.
                var idx = name!.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
                return idx >= 0 ? name[(idx + 1)..] : name;
            }
        }

        return null;
    }

    private IActionResult FieldProblem(string field, string detail)
    {
        var problem = new ProblemDetails
        {
            Type = "https://jeeb.dev/errors/validation",
            Title = "Invalid submission field",
            Detail = detail,
            Status = StatusCodes.Status400BadRequest,
        };
        problem.Extensions["field"] = field;
        return BadRequest(problem);
    }

    private string ResolveIdempotencyKey()
    {
        if (Request.Headers.TryGetValue(IdempotencyHeader, out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            return header.ToString();
        }
        // No key supplied — synthesise a per-request key so each call is distinct
        // (no accidental cross-request dedup).
        return $"req_{Guid.NewGuid():N}";
    }

    // A stable, non-PII proof ref derived from the signature blob. The raw blob is
    // never stored or echoed; only this digest handle references the acceptance.
    private static string DeriveProofRef(string signatureBlob)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(signatureBlob));
        return "sig_" + Convert.ToHexString(hash)[..32].ToLowerInvariant();
    }

    private IActionResult InvalidSignature(string detail) => Problem(
        type: "https://jeeb.dev/errors/invalid-signature",
        title: "Invalid signature",
        detail: detail,
        statusCode: StatusCodes.Status400BadRequest);
}

// --- request / response DTOs (snake_case mobile contract) ------------------

/// <summary>Body for <c>POST /v1/kyc/contract-template/sign</c>.</summary>
public sealed class KycTosSignBody
{
    [JsonPropertyName("template_id")]
    public string? TemplateId { get; init; }

    [JsonPropertyName("tos_version")]
    public string? TosVersion { get; init; }

    [JsonPropertyName("signature_blob")]
    public string? SignatureBlob { get; init; }
}

/// <summary>Response for the ToS sign. Never echoes the raw signature blob.</summary>
public sealed class KycTosSignResponse
{
    [JsonPropertyName("tos_signed_at")]
    public required DateTimeOffset TosSignedAt { get; init; }

    [JsonPropertyName("tos_accepted_version")]
    public required string TosAcceptedVersion { get; init; }
}

/// <summary>Body for the JSON <c>POST /v1/kyc/submit</c> (refs, not bytes).</summary>
public sealed class KycSubmitJsonBody
{
    [JsonPropertyName("id_type")]
    public string? IdType { get; init; }

    [JsonPropertyName("id_number")]
    public string? IdNumber { get; init; }

    [JsonPropertyName("id_document_front_url")]
    public string? IdDocumentFrontUrl { get; init; }

    [JsonPropertyName("id_document_back_url")]
    public string? IdDocumentBackUrl { get; init; }

    [JsonPropertyName("driver_license_number")]
    public string? DriverLicenseNumber { get; init; }

    [JsonPropertyName("driver_license_expiry")]
    public string? DriverLicenseExpiry { get; init; }

    [JsonPropertyName("vehicle_registration_url")]
    public string? VehicleRegistrationUrl { get; init; }

    [JsonPropertyName("vehicle_plate_number")]
    public string? VehiclePlateNumber { get; init; }

    [JsonPropertyName("vehicle_year_make_model")]
    public string? VehicleYearMakeModel { get; init; }

    [JsonPropertyName("selfie_with_liveness_url")]
    public string? SelfieWithLivenessUrl { get; init; }

    [JsonPropertyName("tos_accepted_version")]
    public string? TosAcceptedVersion { get; init; }
}

/// <summary>Response for <c>GET /v1/kyc/status</c> (H7). Pure projection of the
/// owning kyc-service submission view — no gateway-held state.</summary>
public sealed class KycStatusBffResponse
{
    [JsonPropertyName("submissionId")]
    public required string SubmissionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("rejection_reason")]
    public string? RejectionReason { get; init; }

    [JsonPropertyName("tos_signed_at")]
    public DateTimeOffset? TosSignedAt { get; init; }

    [JsonPropertyName("tos_accepted_version")]
    public string? TosAcceptedVersion { get; init; }

    [JsonPropertyName("submitted_at")]
    public DateTimeOffset SubmittedAt { get; init; }

    [JsonPropertyName("reviewed_at")]
    public DateTimeOffset? ReviewedAt { get; init; }
}

/// <summary>Response for the JSON submit. Echoes id_type, the refs, and tos_signed_at.</summary>
public sealed class KycSubmitJsonResponse
{
    [JsonPropertyName("submissionId")]
    public required string SubmissionId { get; init; }

    [JsonPropertyName("state")]
    public required string State { get; init; }

    [JsonPropertyName("id_type")]
    public string? IdType { get; init; }

    [JsonPropertyName("id_document_front_url")]
    public string? IdDocumentFrontUrl { get; init; }

    [JsonPropertyName("id_document_back_url")]
    public string? IdDocumentBackUrl { get; init; }

    [JsonPropertyName("vehicle_registration_url")]
    public string? VehicleRegistrationUrl { get; init; }

    [JsonPropertyName("selfie_with_liveness_url")]
    public string? SelfieWithLivenessUrl { get; init; }

    [JsonPropertyName("tos_signed_at")]
    public DateTimeOffset? TosSignedAt { get; init; }

    [JsonPropertyName("tos_accepted_version")]
    public string? TosAcceptedVersion { get; init; }
}
