using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEBV4-302: proves the earnings-cache invalidation contract that closes the
/// stale-pre-settlement-0 window on GET /v1/jeebers/me/earnings. The controller
/// links each cached earnings window to the jeeber's change token; when a
/// settlement is recorded, SettlementService trips that token and every cached
/// window for that jeeber evicts before the 5-min TTL elapses.
/// </summary>
public sealed class EarningsCacheInvalidatorTests
{
    private const string Jeeber = "jeeber-abc";

    private static (IMemoryCache cache, EarningsCacheInvalidator inv) NewFixture() =>
        (new MemoryCache(new MemoryCacheOptions()), new EarningsCacheInvalidator());

    // Mirrors JeebEarningsController: cache an earnings window linked to the
    // jeeber's invalidation token.
    private static void CacheWindow(
        IMemoryCache cache, EarningsCacheInvalidator inv, string jeeberId, string key, object value)
    {
        var opts = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
        };
        opts.AddExpirationToken(inv.GetChangeToken(jeeberId));
        cache.Set(key, value, opts);
    }

    [Fact]
    public void Invalidate_evicts_every_cached_window_for_that_jeeber()
    {
        var (cache, inv) = NewFixture();

        // Two windows the jeeber queried before being credited (both cached "0").
        CacheWindow(cache, inv, Jeeber, "earnings:jeeber-abc:week:20260714:20260720", 0m);
        CacheWindow(cache, inv, Jeeber, "earnings:jeeber-abc:month:20260701:20260731", 0m);

        cache.TryGetValue("earnings:jeeber-abc:week:20260714:20260720", out _).Should().BeTrue();
        cache.TryGetValue("earnings:jeeber-abc:month:20260701:20260731", out _).Should().BeTrue();

        // Settlement recorded -> SettlementService trips the token.
        inv.Invalidate(Jeeber);

        cache.TryGetValue("earnings:jeeber-abc:week:20260714:20260720", out _).Should().BeFalse();
        cache.TryGetValue("earnings:jeeber-abc:month:20260701:20260731", out _).Should().BeFalse();
    }

    [Fact]
    public void Invalidate_only_affects_the_named_jeeber()
    {
        var (cache, inv) = NewFixture();

        CacheWindow(cache, inv, Jeeber, "earnings:jeeber-abc:week:x", 0m);
        CacheWindow(cache, inv, "jeeber-other", "earnings:jeeber-other:week:x", 42m);

        inv.Invalidate(Jeeber);

        cache.TryGetValue("earnings:jeeber-abc:week:x", out _).Should().BeFalse();
        cache.TryGetValue("earnings:jeeber-other:week:x", out var other).Should().BeTrue();
        other.Should().Be(42m);
    }

    [Fact]
    public void A_window_cached_after_invalidation_survives_the_full_ttl()
    {
        var (cache, inv) = NewFixture();

        // Pre-settlement read caches 0, then settlement invalidates it.
        CacheWindow(cache, inv, Jeeber, "earnings:jeeber-abc:week:x", 0m);
        inv.Invalidate(Jeeber);
        cache.TryGetValue("earnings:jeeber-abc:week:x", out _).Should().BeFalse();

        // The NEXT read (post-settlement) recomputes the real value and re-caches it
        // against a fresh, un-tripped token — it must not be evicted by the prior trip.
        CacheWindow(cache, inv, Jeeber, "earnings:jeeber-abc:week:x", 10m);
        cache.TryGetValue("earnings:jeeber-abc:week:x", out var v).Should().BeTrue();
        v.Should().Be(10m);
    }

    [Fact]
    public void Invalidate_is_a_noop_when_nothing_cached()
    {
        var (_, inv) = NewFixture();
        var act = () => inv.Invalidate("never-seen");
        act.Should().NotThrow();
    }

    /// <summary>
    /// CoW-safe: a settlement recorded concurrently with an earnings read hammers the
    /// same per-jeeber source through cancel+dispose+recreate. The get path must never
    /// throw (e.g. ObjectDisposedException from reading a disposed CTS.Token) and never
    /// hand back null — the retry-on-stale loop is what makes this hold. Deterministic
    /// eviction correctness is covered by the single-threaded tests above.
    /// </summary>
    [Fact]
    public async Task GetChangeToken_is_robust_under_concurrent_invalidation()
    {
        var inv = new EarningsCacheInvalidator();
        const int iterations = 20_000;

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                inv.GetChangeToken(Jeeber).Should().NotBeNull();
            }
        }));

        var invalidators = Enumerable.Range(0, 2).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < iterations; i++)
            {
                inv.Invalidate(Jeeber);
            }
        }));

        var act = async () => await Task.WhenAll(readers.Concat(invalidators));
        await act.Should().NotThrowAsync();
    }
}
