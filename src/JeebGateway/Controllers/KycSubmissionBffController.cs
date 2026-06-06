using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Kyc;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
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
public sealed class KycSubmissionBffController : ControllerBase
{
    private const string IdempotencyHeader = "Idempotency-Key";

    private readonly IKycBffSeam _kyc;
    private readonly IContractSigningServiceClient _contractSigning;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<KycSubmissionBffController> _log;

    public KycSubmissionBffController(
        IKycBffSeam kyc,
        IContractSigningServiceClient contractSigning,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<KycSubmissionBffController> log)
    {
        _kyc = kyc;
        _contractSigning = contractSigning;
        _flags = flags;
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
        string? signatureProofRef;
        if (_flags.CurrentValue.ContractSigning && !string.IsNullOrWhiteSpace(body.TemplateId))
        {
            try
            {
                var signature = await _contractSigning.SignAsync(
                    body.TemplateId!,
                    new SignRequest
                    {
                        RoleKey = "acceptor",
                        PartyRef = userId,
                        SignatureProofRef = DeriveProofRef(body.SignatureBlob!),
                    },
                    ct);
                signatureProofRef = signature.SignatureProofRef ?? DeriveProofRef(body.SignatureBlob!);
            }
            catch (HttpRequestException ex)
            {
                _log.LogWarning(ex, "contract-signing SignAsync failed for user {UserId}", userId);
                return Problem(
                    type: "https://jeeb.dev/errors/upstream-unavailable",
                    title: "Signature ceremony failed",
                    detail: "The contract-signing upstream rejected the signature.",
                    statusCode: StatusCodes.Status502BadGateway);
            }
        }
        else
        {
            signatureProofRef = DeriveProofRef(body.SignatureBlob!);
        }

        // 2) Stamp the KYC ToS-acceptance on the owning kyc-service.
        KycBffTosStampResult stamp;
        try
        {
            stamp = await _kyc.StampTosAsync(userId, body.TosVersion!, signatureProofRef, idempotencyKey, ct);
        }
        catch (KycUpstreamDisabledException)
        {
            return KycUpstreamDisabled();
        }

        return Ok(new KycTosSignResponse
        {
            TosSignedAt = stamp.TosSignedAt,
            TosAcceptedVersion = stamp.TosAcceptedVersion,
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
        // the domain's; here the BFF enforces the four refs are present.
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(body.IdDocumentFrontUrl)) missing.Add("id_document_front_url");
        if (string.IsNullOrWhiteSpace(body.IdDocumentBackUrl)) missing.Add("id_document_back_url");
        if (string.IsNullOrWhiteSpace(body.VehicleRegistrationUrl)) missing.Add("vehicle_registration_url");
        if (string.IsNullOrWhiteSpace(body.SelfieWithLivenessUrl)) missing.Add("selfie_with_liveness_url");
        if (missing.Count > 0)
        {
            return Problem(
                type: "https://jeeb.dev/errors/invalid-request",
                title: "Missing document references",
                detail: $"the following document refs are required: {string.Join(", ", missing)}.",
                statusCode: StatusCodes.Status400BadRequest);
        }

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

        var payload = new KycSubmitJsonResponse
        {
            SubmissionId = result.SubmissionId,
            State = result.State,
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
