using System.Collections.Concurrent;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Seam for fetching the user's ratings (both given and received) at
/// export time. Ratings live in score-taking-service; the packager goes
/// through this interface so it can pull a coherent snapshot regardless
/// of the storage. MVP backs it with <see cref="InMemoryDataExportRatingsProvider"/>.
/// </summary>
public interface IDataExportRatingsProvider
{
    Task<IReadOnlyList<RatingSnapshot>> GetForUserAsync(string userId, CancellationToken ct);
}

public class RatingSnapshot
{
    public required string RatingId { get; init; }
    public required string RequestId { get; init; }
    public required string Direction { get; init; } // "given" or "received"
    public required string CounterpartyId { get; init; }
    public required int Stars { get; init; }
    public string? Comment { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public class InMemoryDataExportRatingsProvider : IDataExportRatingsProvider
{
    private readonly ConcurrentDictionary<string, List<RatingSnapshot>> _byUser = new();

    public Task<IReadOnlyList<RatingSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        if (_byUser.TryGetValue(userId, out var list))
        {
            IReadOnlyList<RatingSnapshot> snapshot = list.OrderBy(r => r.CreatedAt).ToArray();
            return Task.FromResult(snapshot);
        }
        return Task.FromResult<IReadOnlyList<RatingSnapshot>>(Array.Empty<RatingSnapshot>());
    }

    public void Seed(string userId, params RatingSnapshot[] ratings)
    {
        _byUser.AddOrUpdate(
            userId,
            _ => ratings.ToList(),
            (_, existing) =>
            {
                existing.AddRange(ratings);
                return existing;
            });
    }
}
