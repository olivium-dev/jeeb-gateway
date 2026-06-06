using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the net-new <c>kyc-service</c> — the owning microservice for
/// the KYC domain (ADR-0004). kyc-service holds the SM-6 state machine
/// (<c>Draft → Submitted → Verified|Rejected → resubmit</c>), the submission
/// aggregate (document refs, not bytes), the Terms-of-Service acceptance record
/// (<c>tos_signed_at</c> + <c>tos_accepted_version</c>), <c>Idempotency-Key</c>
/// dedup, and the admin review → role-grant <b>DECISION</b>.
///
/// <para>
/// <b>ARCH LAW (ADR-001-rev4, binding).</b> kyc-service calls NO other
/// microservice and shares NO database. It returns a role-grant <i>intent</i>
/// (<c>grantsRole:"jeeber"</c>) on approve; it never mutates user-management.
/// The cross-service composition — form-builder validate, contract-signing
/// sign, cdn signed-PUT broker, and the user-management role append + token
/// re-issue — happens ONLY in this gateway BFF. This client is therefore the
/// gateway's single seam onto the Jeeb KYC domain; the gateway never reaches
/// past it into kyc-service's store.
/// </para>
///
/// <para>
/// <b>DEPLOYMENT STATUS (read before wiring anything live).</b> As of 2026-06-06
/// kyc-service is NOT yet created (the repo <c>olivium-dev/kyc-service</c> and
/// its <c>jeeb_kyc</c> Postgres are owner/SSH-ESCALATED — see ADR-0004 Infra).
/// Its <c>Services:Kyc:BaseUrl</c> in <c>appsettings.Production.json</c> is a
/// clearly-marked PLACEHOLDER (<c>http://192.168.2.50:PORT_TBD/</c>); the
/// feature flag <c>FeatureFlags:UseUpstream:Kyc</c> is therefore DEFAULT-OFF in
/// every environment so the gateway never dials an unroutable host. While the
/// flag is off the BFF controllers keep the legacy in-gateway KYC path (the
/// interim <c>IKycService</c> in-memory seam) so S03 stays exercisable; the
/// fakes are deleted from the live path the moment kyc-service ships and the
/// flag flips. This mirrors the cdn-service / contract-signing-service net-new
/// kill-switch shape exactly.
/// </para>
///
/// <para>
/// <b>CONTRACT NOTE.</b> kyc-service will publish OpenAPI-as-source-of-truth;
/// once it exposes a reachable doc, regenerate this seam via the NSwag pipeline
/// (<c>scripts/regenerate-clients.sh</c>) and keep this interface as the
/// gateway-facing seam. Until then this is a HAND-CODED minimal client over the
/// endpoints S03 requires, following the
/// <see cref="IContractSigningServiceClient"/> / <see cref="ICDNServiceClient"/>
/// precedent for upstreams without an NSwag artifact.
/// </para>
///
/// The named "kyc" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:Kyc:BaseUrl</c>) + the org-standard bearer /
/// X-Service-Auth / Polly resilience pipeline (timeout + breaker, p99 &lt; 800ms;
/// typed 503 on breaker-open, N12), so this class never thinks about
/// retry/timeout/circuit-breaker.
///
/// All methods throw <see cref="HttpRequestException"/> on unexpected non-2xx.
/// </summary>
public interface IKycServiceClient
{
    /// <summary>
    /// Submits a KYC package by reference via
    /// <c>POST /v1/kyc/submissions</c> with an <c>Idempotency-Key</c>. The body
    /// carries document <i>refs</i> (object_refs brokered by the cdn signed-PUT
    /// flow) — never bytes. kyc-service runs SM-6 (<c>Draft → Submitted</c>) and
    /// assigns the durable submission id. A replay of the same Idempotency-Key
    /// returns the SAME submission id (the upstream signals this via the
    /// <see cref="KycSubmitResult.Replayed"/> flag, set from the upstream's
    /// 200-vs-201 status), so the gateway can echo 200 with the original id
    /// (N9 — no duplicate row).
    /// </summary>
    Task<KycSubmitResult> SubmitAsync(
        KycSubmitUpstreamPayload payload,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Stamps the Terms-of-Service acceptance on a submission via
    /// <c>POST /v1/kyc/submissions/{id}/tos-signature</c> with an
    /// <c>Idempotency-Key</c>. kyc-service records <c>tos_signed_at</c> +
    /// <c>tos_accepted_version</c>. A replay returns the ORIGINAL
    /// <c>tos_signed_at</c> (no double-stamp, N10). The gateway calls this only
    /// AFTER contract-signing has recorded the signature (DEC3).
    /// </summary>
    Task<KycTosSignatureResult> StampTosSignatureAsync(
        string submissionId,
        KycTosStampPayload payload,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// ADDITIVE (S03 E1). Records a STANDALONE ToS acceptance keyed by the user
    /// (subject) via <c>POST /v1/kyc/tos-acceptances</c>, for the common case where
    /// the ToS is signed BEFORE any submission exists (H5 precedes H6). Idempotent
    /// in kyc-service: a replay returns the ORIGINAL <c>tos_signed_at</c> (no
    /// re-stamp, no duplicate — N10). The submit path still mirrors the acceptance
    /// onto the submission via <c>tos_accepted_version</c>.
    /// </summary>
    Task<KycTosSignatureResult> StampStandaloneTosAsync(
        string userId,
        KycTosStampPayload payload,
        CancellationToken ct);

    /// <summary>
    /// Reads the latest submission for a user via
    /// <c>GET /v1/kyc/submissions/by-user/{userId}</c>. Returns <c>null</c> on
    /// 404 (the user has no submission yet).
    /// </summary>
    Task<KycSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Reads a single submission by id via
    /// <c>GET /v1/kyc/submissions/{id}</c>. Returns <c>null</c> on 404.
    /// </summary>
    Task<KycSubmissionView?> GetByIdAsync(string submissionId, CancellationToken ct);

    /// <summary>
    /// Reads the pending-review queue via
    /// <c>GET /v1/kyc/submissions?status=pending_review&amp;page=&amp;pageSize=</c>
    /// (H7 / N6 — admin-only at the gateway edge).
    /// </summary>
    Task<KycQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Adjudicates a submission via
    /// <c>PATCH /v1/kyc/submissions/{id}/review</c>. The decision body is
    /// <c>{action: approve|reject|request_resubmit, reason?, resubmitSteps?}</c>.
    /// Returns <c>{status, grantsRole?}</c>: on approve the upstream sets
    /// <c>grantsRole = "jeeber"</c> (the role-grant INTENT — kyc-service does NOT
    /// mutate user-management; the gateway composes that). Re-review of a
    /// finalised row surfaces as <see cref="KycReviewConflictException"/>
    /// (upstream 409, N8).
    /// </summary>
    /// <exception cref="KycReviewConflictException">
    /// The submission is no longer pending (upstream 409).</exception>
    /// <exception cref="KycReviewValidationException">
    /// The decision body is invalid for the chosen action (upstream 400 — e.g.
    /// reject without a reason, request_resubmit without steps).</exception>
    Task<KycReviewDecision> ReviewAsync(
        string submissionId,
        KycReviewDecisionRequest request,
        CancellationToken ct);
}

// --- request / response DTOs (generic vocab; no jeeb/client/jeeber leakage
//     beyond the role-grant intent string the upstream itself emits) ----------

/// <summary>
/// Body for <c>POST /v1/kyc/submissions</c>. Carries document REFS (object_refs
/// from the cdn signed-PUT broker) and the scalar KYC fields — never bytes. The
/// gateway assembles this from the JSON <c>/v1/kyc/submit</c> request after
/// validating against the form-builder render schema.
/// </summary>
public sealed class KycSubmitUpstreamPayload
{
    public required string UserId { get; init; }
    public string? IdType { get; init; }
    public string? IdNumber { get; init; }
    public string? IdDocumentFrontRef { get; init; }
    public string? IdDocumentBackRef { get; init; }
    public string? DriverLicenseNumber { get; init; }
    public string? DriverLicenseExpiry { get; init; }
    public string? VehicleRegistrationRef { get; init; }
    public string? VehiclePlateNumber { get; init; }
    public string? VehicleYearMakeModel { get; init; }
    public string? SelfieWithLivenessRef { get; init; }
    public string? TosAcceptedVersion { get; init; }
}

/// <summary>Result of <see cref="IKycServiceClient.SubmitAsync"/>.</summary>
public sealed class KycSubmitResult
{
    public required string SubmissionId { get; init; }

    /// <summary>The SM-6 state after submit (expected <c>Submitted</c>).</summary>
    public required string State { get; init; }

    /// <summary>The stored ToS acceptance stamp carried back for the echo.</summary>
    public DateTimeOffset? TosSignedAt { get; init; }
    public string? TosAcceptedVersion { get; init; }

    /// <summary>True when the upstream served a replay (200) rather than a new
    /// row (201) — the gateway echoes 200 with the original id (N9).</summary>
    public bool Replayed { get; init; }

    /// <summary>The full upstream submission payload, carried verbatim so the
    /// gateway can echo the document refs without coupling to every field.</summary>
    public JsonElement Document { get; init; }
}

/// <summary>Body for <c>POST /v1/kyc/submissions/{id}/tos-signature</c>.</summary>
public sealed class KycTosStampPayload
{
    public required string TosAcceptedVersion { get; init; }

    /// <summary>The contract-signing-minted proof ref (signature id), NOT the raw
    /// blob — the blob never lands in kyc-service's store.</summary>
    public string? SignatureProofRef { get; init; }
}

/// <summary>Result of <see cref="IKycServiceClient.StampTosSignatureAsync"/>.</summary>
public sealed class KycTosSignatureResult
{
    public required DateTimeOffset TosSignedAt { get; init; }
    public required string TosAcceptedVersion { get; init; }
    public bool Replayed { get; init; }
}

/// <summary>A submission projection (queue item / status read).</summary>
public sealed class KycSubmissionView
{
    public required string SubmissionId { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; init; }
    public string? RejectionReason { get; init; }
    public DateTimeOffset? TosSignedAt { get; init; }
    public string? TosAcceptedVersion { get; init; }
    public IReadOnlyList<string> ResubmitSteps { get; init; } = Array.Empty<string>();

    /// <summary>The full upstream payload (document refs, etc.), unmodified.</summary>
    public JsonElement Document { get; init; }
}

/// <summary>A page of the pending-review queue.</summary>
public sealed class KycQueuePage
{
    public required IReadOnlyList<KycSubmissionView> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>The three review actions the admin reviewer can take.</summary>
public enum KycReviewActionKind
{
    Approve,
    Reject,
    RequestResubmit
}

/// <summary>Body for <c>PATCH /v1/kyc/submissions/{id}/review</c>.</summary>
public sealed class KycReviewDecisionRequest
{
    public required KycReviewActionKind Action { get; init; }
    public string? ReviewerId { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string>? ResubmitSteps { get; init; }
}

/// <summary>
/// Result of <see cref="IKycServiceClient.ReviewAsync"/>. <see cref="GrantsRole"/>
/// is the role-grant INTENT the gateway acts on (composes the UM append). It is
/// non-null only on an approve outcome.
/// </summary>
public sealed class KycReviewDecision
{
    public required string SubmissionId { get; init; }
    public string? UserId { get; init; }
    public required string Status { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<string> ResubmitSteps { get; init; } = Array.Empty<string>();

    /// <summary>The opaque role the gateway should append in user-management
    /// (e.g. <c>"jeeber"</c>); null for reject / request_resubmit.</summary>
    public string? GrantsRole { get; init; }
}

/// <summary>The upstream rejected a re-review of a finalised submission (409, N8).</summary>
public sealed class KycReviewConflictException : Exception
{
    public KycReviewConflictException(string submissionId, string? detail)
        : base(detail ?? $"kyc submission '{submissionId}' is no longer pending review.")
    {
    }
}

/// <summary>The upstream rejected the review body for the chosen action (400).</summary>
public sealed class KycReviewValidationException : Exception
{
    public KycReviewValidationException(string detail) : base(detail)
    {
    }
}
