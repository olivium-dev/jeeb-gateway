namespace JeebGateway.Kyc;

/// <summary>
/// Lifecycle states for a KYC submission (T-backend-004). A Jeeber must
/// clear KYC before any deliveries are matched, so the gateway gates the
/// availability toggle on a <c>verified</c> row in the queue.
/// </summary>
///   pending_review — user uploaded ID front/back, selfie, and vehicle
///     details. The submission is in the moderation queue waiting on an
///     admin reviewer. This is the only state POST /kyc/submit can
///     produce in the MVP.
///   verified — admin reviewer approved the submission. The Jeeber can
///     go online.
///   rejected — admin reviewer rejected the submission. The Jeeber can
///     re-submit; the previous documents stay in encrypted storage so
///     the audit trail survives.
public static class KycStatus
{
    public const string PendingReview = "pending_review";
    public const string Verified = "verified";
    public const string Rejected = "rejected";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        PendingReview,
        Verified,
        Rejected
    };
}

/// <summary>
/// One row per Jeeber per attempt. The most recent row is the source of
/// truth for GET /kyc/status — older attempts are retained so the admin
/// review queue can show resubmission history.
/// </summary>
public class KycSubmission
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Status { get; set; }
    public required DateTimeOffset SubmittedAt { get; init; }
    public DateTimeOffset? ReviewedAt { get; set; }
    public string? ReviewerId { get; set; }
    public string? RejectionReason { get; set; }

    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }

    /// <summary>
    /// Pointers into <see cref="IKycDocumentStorage"/>. The bytes never
    /// hang off the submission row directly — they are encrypted at rest
    /// in the document store, and the row keeps only the opaque handles.
    /// </summary>
    public required string IdFrontDocumentId { get; init; }
    public required string IdBackDocumentId { get; init; }
    public required string SelfieDocumentId { get; init; }

    /// <summary>
    /// Verdict from the liveness check stub. False means the selfie did
    /// not pass — the row is still queued because the admin reviewer
    /// makes the final call, but the verdict surfaces in the GET response
    /// so the mobile app can prompt the user to retake the selfie.
    /// </summary>
    public required bool LivenessPassed { get; init; }
}
