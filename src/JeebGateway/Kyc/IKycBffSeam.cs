namespace JeebGateway.Kyc;

/// <summary>
/// The gateway BFF's single seam onto the KYC <b>domain</b> for the S03 JSON
/// flow (submit-by-ref, ToS stamp, review-with-grant-intent). This is the
/// boundary that hides the ADR-0004 transition: when
/// <c>FeatureFlags:UseUpstream:Kyc</c> is ON it delegates to the owning
/// <c>kyc-service</c> via <see cref="JeebGateway.Services.Clients.IKycServiceClient"/>;
/// while OFF (kyc-service not yet deployed — repo/DB ESCALATED) it delegates to
/// the interim in-gateway store so S03 stays exercisable. The BFF controllers
/// compose this seam with form-builder / contract-signing / cdn / user-management
/// and never branch on the flag themselves.
///
/// <para>
/// IMPORTANT (ADR-0004 / guardrail #3). The interim in-gateway path is a
/// TEMPORARY kill-switch fallback, not the destination. The in-memory KYC store
/// and the document fakes are deleted from the live path the moment kyc-service
/// ships and the flag flips — at which point this seam routes 100% to the owning
/// service and the gateway holds zero KYC state. This seam exists precisely so
/// that flip is a one-line config change, not a controller rewrite.
/// </para>
/// </summary>
public interface IKycBffSeam
{
    /// <summary>True when the live path targets the owning kyc-service.</summary>
    bool UpstreamEnabled { get; }

    /// <summary>
    /// Submit a KYC package BY REFERENCE (object_refs from the cdn signed-PUT
    /// broker; no bytes). Idempotent on <paramref name="idempotencyKey"/>: a
    /// replay returns the SAME submission with <see cref="KycBffSubmitResult.Replayed"/>
    /// = true and zero new side effects (N9).
    /// </summary>
    Task<KycBffSubmitResult> SubmitByRefAsync(
        KycBffSubmitInput input,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Stamp the ToS acceptance on the caller's latest submission (or a standalone
    /// acceptance record when there is no submission yet). Idempotent on
    /// <paramref name="idempotencyKey"/>: a replay returns the ORIGINAL
    /// <see cref="KycBffTosStampResult.TosSignedAt"/> (no double-stamp, N10).
    /// The raw signature blob is NEVER persisted here — only the
    /// <paramref name="signatureProofRef"/> minted by contract-signing.
    /// </summary>
    Task<KycBffTosStampResult> StampTosAsync(
        string userId,
        string tosAcceptedVersion,
        string? signatureProofRef,
        string idempotencyKey,
        CancellationToken ct);

    /// <summary>
    /// Read the caller's latest submission for the KYC status surface (H7 / N7).
    /// Returns <c>null</c> when the user has no submission yet (the controller
    /// maps that to 404).
    /// </summary>
    Task<KycBffSubmissionView?> GetLatestForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Read the pending-review queue oldest-first (H7), paginated. Admin-gated at
    /// the gateway edge.
    /// </summary>
    Task<KycBffQueuePage> GetPendingQueueAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// Adjudicate a submission. Returns the new status plus the role-grant INTENT
    /// (<see cref="KycBffReviewResult.GrantsRole"/>, non-null only on approve). The
    /// seam NEVER mutates user-management — the gateway composes that.
    /// </summary>
    /// <exception cref="KycBffReviewConflictException">Re-review of a finalised row (409, N8).</exception>
    /// <exception cref="KycBffReviewValidationException">Invalid body for the action (400).</exception>
    /// <exception cref="KycBffNotFoundException">No such submission (404).</exception>
    Task<KycBffReviewResult> ReviewAsync(
        string submissionId,
        KycBffReviewInput input,
        CancellationToken ct);
}

/// <summary>Submit-by-ref input assembled by the BFF after form-builder validation.</summary>
public sealed class KycBffSubmitInput
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

public sealed class KycBffSubmitResult
{
    public required string SubmissionId { get; init; }
    public required string State { get; init; }
    public DateTimeOffset? TosSignedAt { get; init; }
    public string? TosAcceptedVersion { get; init; }
    public bool Replayed { get; init; }
}

public sealed class KycBffTosStampResult
{
    public required DateTimeOffset TosSignedAt { get; init; }
    public required string TosAcceptedVersion { get; init; }
    public bool Replayed { get; init; }
}

/// <summary>A submission projection for the KYC status surface (H7 / N7).</summary>
public sealed class KycBffSubmissionView
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
}

/// <summary>One pending-review queue item projected for the admin surface.</summary>
public sealed class KycBffQueueItem
{
    public required string SubmissionId { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}

public sealed class KycBffQueuePage
{
    public required IReadOnlyList<KycBffQueueItem> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>The three review actions the admin reviewer can take. Relocated here
/// from the deleted legacy in-gateway KYC domain (IKycService) — the gateway now
/// holds ZERO KYC business state, only this BFF seam vocabulary.</summary>
public enum KycReviewAction
{
    Approve,
    Reject,
    RequestResubmit
}

public sealed class KycBffReviewInput
{
    public required KycReviewAction Action { get; init; }
    public required string ReviewerId { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string>? ResubmitSteps { get; init; }
}

public sealed class KycBffReviewResult
{
    public required string SubmissionId { get; init; }

    /// <summary>The owning user (the aspiring jeeber) — used by the gateway to
    /// compose the UM role append on approve. Never surfaced in the admin response.</summary>
    public string? UserId { get; init; }

    public required string Status { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<string> ResubmitSteps { get; init; } = Array.Empty<string>();

    /// <summary>The opaque role to append in user-management on approve; null otherwise.</summary>
    public string? GrantsRole { get; init; }

    /// <summary>
    /// True when a status-change push was already delivered as part of the review
    /// (interim path fires it inline). On the upstream path notification is
    /// composed async off the critical path (N14), so this is false there.
    /// </summary>
    public bool PushSent { get; init; }
}

public sealed class KycBffReviewConflictException : Exception
{
    public KycBffReviewConflictException(string message) : base(message) { }
}

public sealed class KycBffReviewValidationException : Exception
{
    public KycBffReviewValidationException(string message) : base(message) { }
}

public sealed class KycBffNotFoundException : Exception
{
    public KycBffNotFoundException(string message) : base(message) { }
}
