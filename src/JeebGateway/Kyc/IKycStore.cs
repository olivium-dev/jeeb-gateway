namespace JeebGateway.Kyc;

/// <summary>
/// Persistence seam for KYC submissions (T-backend-004). The MVP backs
/// this with an in-memory store; production swaps in a Postgres-backed
/// implementation that lives next to the admin moderation queue.
/// </summary>
public interface IKycStore
{
    Task<KycSubmission> AddAsync(KycSubmission submission, CancellationToken ct);

    /// <summary>
    /// Returns the most recent submission for a user, regardless of
    /// status. Older attempts stay in the store so an admin reviewer can
    /// see the resubmission history.
    /// </summary>
    Task<KycSubmission?> GetLatestForUserAsync(string userId, CancellationToken ct);

    Task<IReadOnlyList<KycSubmission>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// T-backend-005. Fetch a single submission by id so the admin review
    /// endpoint can look it up before mutating its status. Returns null
    /// when no such submission exists.
    /// </summary>
    Task<KycSubmission?> GetByIdAsync(string submissionId, CancellationToken ct);

    /// <summary>
    /// T-backend-005. Paginated queue of submissions awaiting admin
    /// review, oldest-first so the reviewer drains the queue in
    /// submission order (AC #1).
    /// </summary>
    Task<KycQueuePage> ListPendingForReviewAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>
    /// T-backend-005. Persists the reviewer's decision on
    /// <paramref name="submissionId"/> and returns the updated row, or
    /// null when no such submission exists. The service layer owns the
    /// downstream side-effects (role unlock, push, audit log) so this
    /// store stays a thin persistence seam.
    /// </summary>
    Task<KycSubmission?> ApplyReviewAsync(string submissionId, KycReviewPatch patch, CancellationToken ct);
}

/// <summary>
/// One page of the moderation queue. Total is the unfiltered count so
/// the admin UI can render a pager.
/// </summary>
public class KycQueuePage
{
    public required IReadOnlyList<KycSubmission> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int Total { get; init; }
}

/// <summary>
/// Mutation payload for <see cref="IKycStore.ApplyReviewAsync"/>.
/// </summary>
public class KycReviewPatch
{
    public required string Status { get; init; }
    public required DateTimeOffset ReviewedAt { get; init; }
    public required string ReviewerId { get; init; }
    public string? RejectionReason { get; init; }
    public IReadOnlyList<string>? ResubmitSteps { get; init; }
}
