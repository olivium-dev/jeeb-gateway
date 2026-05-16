using System.Collections.Concurrent;

namespace JeebGateway.Kyc;

/// <summary>
/// MVP in-memory implementation of <see cref="IKycStore"/>. Mirrors the
/// lifecycle the Postgres-backed store will implement so the controller,
/// service, and tests share a single seam.
/// </summary>
public class InMemoryKycStore : IKycStore
{
    private readonly ConcurrentDictionary<string, KycSubmission> _byId = new();

    public Task<KycSubmission> AddAsync(KycSubmission submission, CancellationToken ct)
    {
        _byId[submission.Id] = submission;
        return Task.FromResult(submission);
    }

    public Task<KycSubmission?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        var latest = _byId.Values
            .Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal))
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<KycSubmission>> ListForUserAsync(string userId, CancellationToken ct)
    {
        var items = _byId.Values
            .Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal))
            .OrderByDescending(s => s.SubmittedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<KycSubmission>>(items);
    }
}
