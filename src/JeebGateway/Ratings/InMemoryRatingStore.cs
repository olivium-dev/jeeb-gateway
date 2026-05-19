using System.Collections.Concurrent;

namespace JeebGateway.Ratings;

/// <summary>
/// MVP in-memory implementation of <see cref="IRatingStore"/>. Backs the
/// mutual-blind state machine until the Postgres-backed implementation
/// lands alongside the downstream score-taking-service migration.
/// </summary>
public sealed class InMemoryRatingStore : IRatingStore
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

    public Task<IReadOnlyList<RatingPair>> ListPendingAutoRevealAsync(
        DateTimeOffset asOf,
        TimeSpan ratingWindow,
        CancellationToken ct)
    {
        // Snapshot under the same lock the writers use so we don't read a
        // half-mutated row mid-Submit. Cheap because the MVP store is small;
        // the Postgres swap will replace this with a parameterised SELECT
        // that filters on (auto_revealed_at IS NULL AND delivered_at <= $1
        // AND NOT (client_rating IS NOT NULL AND jeeber_rating IS NOT NULL)).
        lock (_writeLock)
        {
            var result = new List<RatingPair>();
            foreach (var pair in _pairs.Values)
            {
                if (pair.AutoRevealedAt is not null) continue;
                if (pair.ClientRating is not null && pair.JeeberRating is not null) continue;
                if (pair.DeliveredAt + ratingWindow > asOf) continue;
                result.Add(pair);
            }
            return Task.FromResult<IReadOnlyList<RatingPair>>(result);
        }
    }

    public Task<bool> TryMarkAutoRevealedAsync(
        string deliveryId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_pairs.TryGetValue(deliveryId, out var pair))
            {
                // Row vanished between snapshot and stamp — treat as "no-op,
                // not stamped by me" so the cron skips notification.
                return Task.FromResult(false);
            }

            if (pair.AutoRevealedAt is not null)
            {
                // Idempotent — second cron pass observes the stamp and bails.
                return Task.FromResult(false);
            }

            pair.AutoRevealedAt = at;
            return Task.FromResult(true);
        }
    }
}
