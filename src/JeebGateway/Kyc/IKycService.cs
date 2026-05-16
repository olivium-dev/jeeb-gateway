namespace JeebGateway.Kyc;

/// <summary>
/// Orchestrates the KYC submission flow (T-backend-004):
///   1. persist each uploaded document via <see cref="IKycDocumentStorage"/>
///      so the bytes land in encrypted storage,
///   2. run the liveness check stub on the selfie,
///   3. write the queue entry with status <c>pending_review</c> so the
///      admin moderation queue can pick it up.
/// </summary>
public interface IKycService
{
    Task<KycSubmission> SubmitAsync(KycSubmissionInput input, CancellationToken ct);

    Task<KycSubmission?> GetLatestStatusAsync(string userId, CancellationToken ct);

    /// <summary>
    /// T-backend-005. Apply an admin reviewer's decision to a queued
    /// submission. On <see cref="KycReviewAction.Approve"/> the Jeeber
    /// role is granted to the submitter (AC #2, within 5s); on
    /// <see cref="KycReviewAction.Reject"/> a push notification with the
    /// rejection reason is fired so the user knows they can resubmit
    /// (AC #3); on <see cref="KycReviewAction.RequestResubmit"/> only
    /// the specific document steps in <paramref name="input"/> are
    /// reopened (AC #4). Returns null when no submission with that id
    /// exists. Throws <see cref="KycReviewValidationException"/> when
    /// the input violates the action's invariants (e.g. reject without
    /// a reason, resubmit without document steps).
    /// </summary>
    Task<KycReviewOutcome?> ReviewAsync(string submissionId, KycReviewInput input, CancellationToken ct);
}

public enum KycReviewAction
{
    Approve,
    Reject,
    RequestResubmit
}

public class KycReviewInput
{
    public required KycReviewAction Action { get; init; }
    public required string ReviewerId { get; init; }
    public string? Reason { get; init; }
    public IReadOnlyList<string>? ResubmitSteps { get; init; }
}

/// <summary>
/// Result envelope so the controller can surface what changed without
/// re-reading the row. <see cref="RoleGranted"/> is true when the
/// approve path actually added the Jeeber role (false if the user
/// already held it).
/// </summary>
public class KycReviewOutcome
{
    public required KycSubmission Submission { get; init; }
    public required bool RoleGranted { get; init; }
    public required bool PushSent { get; init; }
}

public class KycReviewValidationException : Exception
{
    public KycReviewValidationException(string message) : base(message) { }
}

public class KycSubmissionInput
{
    public required string UserId { get; init; }
    public required string VehicleType { get; init; }
    public required string VehicleRegistration { get; init; }
    public required KycDocumentInput IdFront { get; init; }
    public required KycDocumentInput IdBack { get; init; }
    public required KycDocumentInput Selfie { get; init; }
}

public class KycDocumentInput
{
    public required string FileName { get; init; }
    public required string ContentType { get; init; }
    public required byte[] Bytes { get; init; }
}
