using System.Collections.Concurrent;

namespace JeebGateway.Matching;

/// <summary>
/// MVP-grade in-memory ratings store. Tests seed values via
/// <see cref="SetRating"/>; production swaps in a ratings-service
/// client behind the same <see cref="IJeeberRatingProvider"/> contract.
/// </summary>
public sealed class InMemoryJeeberRatingProvider : IJeeberRatingProvider
{
    private readonly ConcurrentDictionary<string, double> _ratings = new(StringComparer.Ordinal);

    public Task<double> GetRatingAsync(string userId, CancellationToken ct)
    {
        return Task.FromResult(_ratings.TryGetValue(userId, out var r) ? r : 0.0);
    }

    public Task<IReadOnlyDictionary<string, double>> GetRatingsAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken ct)
    {
        var result = new Dictionary<string, double>(userIds.Count, StringComparer.Ordinal);
        foreach (var id in userIds)
        {
            result[id] = _ratings.TryGetValue(id, out var r) ? r : 0.0;
        }
        return Task.FromResult<IReadOnlyDictionary<string, double>>(result);
    }

    /// <summary>
    /// Test/admin helper — seed a Jeeber's rating. The production store
    /// is fed by the ratings-service rolling-average pipeline.
    /// </summary>
    public void SetRating(string userId, double rating) => _ratings[userId] = rating;
}
