using FluentAssertions;
using JeebGateway.Requests.OtpHandover;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace JeebGateway.IntegrationTests.Requests.OtpHandover;

/// <summary>
/// JEBV4-83 (F7) — <see cref="DistributedCacheHandoverCodeStore.InvalidateAsync"/> must
/// clear the Gap-G4 in-app handover code after a successful handover so the raw secret
/// does not live out its full 24h TTL as a stale, still-matchable code, AND must
/// degrade-don't-fail on a cache-infrastructure fault (the handover already committed
/// upstream — a Redis blip on cleanup must never turn the verified 200 into a 5xx).
/// </summary>
public class HandoverCodeInvalidateTests
{
    private static DistributedCacheHandoverCodeStore StoreOver(IDistributedCache cache) =>
        new(cache, NullLogger<DistributedCacheHandoverCodeStore>.Instance);

    private static IDistributedCache NewMemoryCache() =>
        new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));

    [Fact]
    public async Task InvalidateAsync_After_Issue_Clears_The_Stored_Code()
    {
        var store = StoreOver(NewMemoryCache());

        var code = await store.IssueAsync("delivery-inv-1", CancellationToken.None);
        (await store.GetAsync("delivery-inv-1", CancellationToken.None)).Should().Be(code);
        (await store.TryMatchAsync("delivery-inv-1", code, CancellationToken.None)).Should().BeTrue();

        await store.InvalidateAsync("delivery-inv-1", CancellationToken.None);

        // F7 AC: after a successful verify, otp:handovercode:{deliveryId} is cleared;
        // a re-read returns nothing and the once-valid code no longer matches.
        (await store.GetAsync("delivery-inv-1", CancellationToken.None)).Should().BeNull();
        (await store.TryMatchAsync("delivery-inv-1", code, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidateAsync_When_Nothing_Stored_Is_A_NoOp()
    {
        var store = StoreOver(NewMemoryCache());

        var act = () => store.InvalidateAsync("never-issued", CancellationToken.None);

        await act.Should().NotThrowAsync("invalidate is idempotent and a no-op when no code is stored");
    }

    [Fact]
    public async Task InvalidateAsync_On_Redis_Fault_Degrades_Does_Not_Throw()
    {
        // A Redis blip on the post-verify cleanup must never fail the already-committed,
        // already-verified handover response — the stale code self-heals via its 24h TTL.
        var store = StoreOver(new FaultingDistributedCache());

        var act = () => store.InvalidateAsync("delivery-fault", CancellationToken.None);

        await act.Should().NotThrowAsync(
            "a cache-infrastructure fault on invalidate must degrade, not 500 the money-adjacent verify path");
    }

    /// <summary>
    /// Minimal <see cref="IDistributedCache"/> whose every member throws
    /// <see cref="RedisConnectionException"/> — the fault surface the
    /// StackExchangeRedis cache raises when the server is unreachable (same fake shape as
    /// <c>DistributedCacheHandoverCodeStoreFailOpenTests</c>).
    /// </summary>
    private sealed class FaultingDistributedCache : IDistributedCache
    {
        private static RedisConnectionException NewFault() =>
            new(ConnectionFailureType.UnableToConnect, "simulated Redis outage (JEBV4-83 F7 test fake)");

        public byte[]? Get(string key) => throw NewFault();
        public Task<byte[]?> GetAsync(string key, CancellationToken token = default) => throw NewFault();
        public void Refresh(string key) => throw NewFault();
        public Task RefreshAsync(string key, CancellationToken token = default) => throw NewFault();
        public void Remove(string key) => throw NewFault();
        public Task RemoveAsync(string key, CancellationToken token = default) => throw NewFault();
        public void Set(string key, byte[] value, DistributedCacheEntryOptions options) => throw NewFault();
        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default) => throw NewFault();
    }
}
