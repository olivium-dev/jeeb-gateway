using System.Collections.Concurrent;

namespace JeebGateway.Ratings;

/// <summary>
/// MVP in-memory implementation of <see cref="IRatingStore"/>. Backs the
/// mutual-blind state machine; the optional feedback-service-backed store
/// (behind <c>FeatureFlags:UseUpstream:Ratings</c>) is the upstream swap.
/// </summary>
public sealed class InMemoryRatingStore : IRatingStoreExtended
{
    private readonly ConcurrentDictionary<string, RatingPair> _pairs = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public Task<RatingPair?> GetAsync(string deliveryId, CancellationToken ct)
    {
        _pairs.TryGetValue(deliveryId, out var pair);
        return Task.FromResult<RatingPair?>(pair);
    }

    public Task<RatingPair> EnsureAsync(
        string deliveryId,
        string clientId,
        string jeeberId,
        DateTimeOffset deliveredAt,
        CancellationToken ct)
    {
        var pair = _pairs.GetOrAdd(deliveryId, _ => new RatingPair
        {
            DeliveryId = deliveryId,
            ClientId = clientId,
            JeeberId = jeeberId,
            DeliveredAt = deliveredAt
        });
        return Task.FromResult(pair);
    }

    public Task<RatingPair> SubmitAsync(
        string deliveryId,
        bool callerIsClient,
        RatingEntry entry,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_pairs.TryGetValue(deliveryId, out var pair))
            {
                throw new InvalidOperationException(
                    $"Rating row for delivery {deliveryId} has not been initialised. Call EnsureAsync first.");
            }

            if (callerIsClient)
            {
                if (pair.ClientRating is not null)
                {
                    throw new InvalidOperationException(
                        $"Client has already rated delivery {deliveryId}.");
                }
                pair.ClientRating = entry;
            }
            else
            {
                if (pair.JeeberRating is not null)
                {
                    throw new InvalidOperationException(
                        $"Jeeber has already rated delivery {deliveryId}.");
                }
                pair.JeeberRating = entry;
            }

            return Task.FromResult(pair);
        }
    }

    public Task<RatingWindowSweepResult> SweepExpiredWindowsAsync(
        DateTimeOffset deliveredAtCutoff,
        DateTimeOffset processedAt,
        CancellationToken ct)
    {
        var revealed = 0;
        var closed = 0;

        lock (_writeLock)
        {
            foreach (var pair in _pairs.Values)
            {
                ct.ThrowIfCancellationRequested();

                if (pair.RevealedAt is not null || pair.WindowClosedAt is not null)
                {
                    continue;
                }

                // The submit path treats exactly deliveredAt + window as still open.
                if (pair.DeliveredAt >= deliveredAtCutoff)
                {
                    continue;
                }

                if (pair.ClientRating is not null && pair.JeeberRating is not null)
                {
                    pair.RevealedAt = processedAt;
                    revealed++;
                }
                else
                {
                    pair.WindowClosedAt = processedAt;
                    closed++;
                }
            }
        }

        return Task.FromResult(new RatingWindowSweepResult(revealed, closed));
    }

    public Task<IReadOnlyList<JeeberRatingSummary>> ListJeebersBelowAverageAsync(
        double threshold,
        int minRatings,
        CancellationToken ct)
    {
        List<JeeberRatingSummary> summaries;

        lock (_writeLock)
        {
            summaries = _pairs.Values
                .Where(pair => pair.RevealedAt is not null && pair.ClientRating is not null)
                .GroupBy(pair => pair.JeeberId, StringComparer.Ordinal)
                .Select(group => new JeeberRatingSummary(
                    JeeberId: group.Key,
                    AverageScore: group.Average(pair => pair.ClientRating!.Stars),
                    RatingCount: group.Count()))
                .Where(summary => summary.RatingCount >= minRatings && summary.AverageScore < threshold)
                .ToList();
        }

        return Task.FromResult<IReadOnlyList<JeeberRatingSummary>>(summaries);
    }
}
