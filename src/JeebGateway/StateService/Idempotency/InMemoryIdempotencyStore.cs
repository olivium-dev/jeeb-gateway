using System.Collections.Concurrent;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// LOCAL/CI-ONLY in-process fallback for <see cref="IIdempotencyStore"/>, used only when
/// jeeb-state-service is NOT wired (<c>JeebStateService:BaseUrl</c> unset / <c>Enabled=false</c>)
/// so the gateway still stands up for local dev + tests without a live state-service. In
/// production the <see cref="StateServiceIdempotencyStore"/> is registered instead and every
/// row lives in jeeb-state-service (ADR-0001/0005) — this class is never on the production path.
///
/// <para>Honors the SAME insert-once contract as the durable store
/// (<c>INSERT … ON CONFLICT (key) DO NOTHING RETURNING</c>): the first writer of a key wins and
/// gets <c>Inserted=true</c>; every later writer/reader replays the ORIGINAL stored status+body
/// with <c>Inserted=false</c>. TTL is best-effort (lazily evicted on access).</para>
/// </summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed record Entry(int StatusCode, string ResponseBodyJson, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, Entry> _store = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    public InMemoryIdempotencyStore(TimeProvider clock) => _clock = clock;

    public Task<IdempotencyOutcome> PutOrGetAsync(
        string key,
        int statusCode,
        string responseBodyJson,
        int ttlSeconds,
        CancellationToken ct)
    {
        EvictIfExpired(key);

        var now = _clock.GetUtcNow();
        var expiresAt = now.AddSeconds(ttlSeconds <= 0 ? 0 : ttlSeconds);
        var inserted = true;

        var entry = _store.AddOrUpdate(
            key,
            _ => new Entry(statusCode, responseBodyJson, expiresAt),
            (_, existing) =>
            {
                inserted = false; // ON CONFLICT DO NOTHING — keep the original.
                return existing;
            });

        return Task.FromResult(new IdempotencyOutcome
        {
            Inserted = inserted,
            StatusCode = entry.StatusCode,
            ResponseBodyJson = entry.ResponseBodyJson,
        });
    }

    public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
    {
        EvictIfExpired(key);

        if (_store.TryGetValue(key, out var entry))
        {
            return Task.FromResult<IdempotencyOutcome?>(new IdempotencyOutcome
            {
                Inserted = false,
                StatusCode = entry.StatusCode,
                ResponseBodyJson = entry.ResponseBodyJson,
            });
        }

        return Task.FromResult<IdempotencyOutcome?>(null);
    }

    public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var results = _store
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal) && kvp.Value.ExpiresAt > now)
            .OrderByDescending(kvp => kvp.Value.ExpiresAt)
            .Select(kvp => new IdempotencyOutcome
            {
                Inserted = false,
                StatusCode = kvp.Value.StatusCode,
                ResponseBodyJson = kvp.Value.ResponseBodyJson,
            })
            .ToList();
        return Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(results);
    }

    private void EvictIfExpired(string key)
    {
        if (_store.TryGetValue(key, out var entry) && entry.ExpiresAt <= _clock.GetUtcNow())
        {
            _store.TryRemove(key, out _);
        }
    }
}
