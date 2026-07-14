using System.Collections.Concurrent;
using Microsoft.Extensions.Primitives;

namespace JeebGateway.Financials;

/// <summary>
/// JEBV4-302: bridges the write side (a settlement being recorded in
/// <see cref="SettlementService"/>) to the read side (the per-jeeber earnings
/// projection cached by <c>JeebEarningsController</c> for up to 5 min).
///
/// <para><b>Why not just delete the cache keys?</b> <see cref="Microsoft.Extensions.Caching.Memory.IMemoryCache"/>
/// exposes no key enumeration and the earnings key space is unbounded
/// (<c>earnings:{jeeberId}:{period}:{from}:{to}</c> across every period/window a
/// jeeber has ever queried), so the recorded keys cannot be reconstructed to
/// remove them. Instead every earnings cache entry is linked to a per-jeeber
/// <see cref="IChangeToken"/> via <c>MemoryCacheEntryOptions.AddExpirationToken</c>;
/// <see cref="Invalidate"/> trips that token and the cache evicts <i>all</i> of
/// that jeeber's entries at once — closing the window where a pre-settlement
/// cached <c>0</c> would otherwise serve for the remaining TTL right after the
/// jeeber was credited.</para>
///
/// <para>Registered as a singleton so the controller (read) and the settlement
/// service (write) share one token registry. Thread-safe: a cancelled/disposed
/// source is never handed back to the read side (the get path retries), so a
/// settlement recorded concurrently with an earnings read can never leave a
/// freshly cached entry pinned to an already-tripped token.</para>
/// </summary>
public interface IEarningsCacheInvalidator
{
    /// <summary>
    /// Returns the live change token for <paramref name="jeeberId"/>, creating one
    /// on first use. Attach it to the earnings cache entry so a later
    /// <see cref="Invalidate"/> evicts that entry. Always returns an un-tripped token.
    /// </summary>
    IChangeToken GetChangeToken(string jeeberId);

    /// <summary>
    /// Evicts every earnings cache entry linked to <paramref name="jeeberId"/> by
    /// tripping the current token. Idempotent and a no-op when nothing is cached.
    /// </summary>
    void Invalidate(string jeeberId);
}

/// <inheritdoc cref="IEarningsCacheInvalidator" />
public sealed class EarningsCacheInvalidator : IEarningsCacheInvalidator
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _sources =
        new(StringComparer.Ordinal);

    public IChangeToken GetChangeToken(string jeeberId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jeeberId);

        while (true)
        {
            var cts = _sources.GetOrAdd(jeeberId, static _ => new CancellationTokenSource());
            try
            {
                // Reading .Token throws ObjectDisposedException if a concurrent
                // Invalidate already disposed this source; IsCancellationRequested
                // guards the already-cancelled-not-yet-removed race. Either way we
                // drop the stale source and retry so the caller always links to a
                // fresh, un-tripped token.
                if (!cts.IsCancellationRequested)
                {
                    return new CancellationChangeToken(cts.Token);
                }
            }
            catch (ObjectDisposedException)
            {
                // fall through to remove + retry
            }

            _sources.TryRemove(new KeyValuePair<string, CancellationTokenSource>(jeeberId, cts));
        }
    }

    public void Invalidate(string jeeberId)
    {
        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return;
        }

        if (_sources.TryRemove(jeeberId, out var cts))
        {
            // Cancel first (trips every linked change token → cache evicts), then
            // dispose. A concurrent GetChangeToken that raced past TryRemove holds a
            // reference to this same source and simply retries when it sees the
            // cancellation/disposal.
            cts.Cancel();
            cts.Dispose();
        }
    }
}
