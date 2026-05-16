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
}
