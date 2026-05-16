using System.Collections.Concurrent;

namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// MVP in-memory implementation of <see cref="IJeeberRestrictionStore"/>.
/// One row per Jeeber holding the current restriction expiry. Reads are
/// lock-free; writes go through <see cref="ConcurrentDictionary{TKey,TValue}.AddOrUpdate"/>.
/// </summary>
public sealed class InMemoryJeeberRestrictionStore : IJeeberRestrictionStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _expiries = new(StringComparer.Ordinal);

    public Task<bool> IsRestrictedAsync(string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        if (!_expiries.TryGetValue(jeeberId, out var expiry)) return Task.FromResult(false);
        return Task.FromResult(at < expiry);
    }

    public Task<DateTimeOffset?> GetActiveExpiryAsync(string jeeberId, DateTimeOffset at, CancellationToken ct)
    {
        if (!_expiries.TryGetValue(jeeberId, out var expiry) || at >= expiry)
        {
            return Task.FromResult<DateTimeOffset?>(null);
        }
        return Task.FromResult<DateTimeOffset?>(expiry);
    }

    public Task ApplyAsync(string jeeberId, DateTimeOffset at, TimeSpan duration, CancellationToken ct)
    {
        _expiries[jeeberId] = at + duration;
        return Task.CompletedTask;
    }
}
