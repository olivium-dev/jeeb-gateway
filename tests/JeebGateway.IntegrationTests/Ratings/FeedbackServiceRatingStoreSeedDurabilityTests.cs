using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Ratings;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Ratings;

/// <summary>
/// F3 (durability hardening) — the per-delivery party/anchor seed the
/// feedback-service-backed rating store keeps (clientId, jeeberId, deliveredAt) was an
/// in-memory <c>ConcurrentDictionary</c>, so after a gateway bounce a rating GET
/// returned null and a rating SUBMIT hard-threw for any delivery whose window opened
/// before the restart. The fix mirrors the seed into the durable idempotency KV on the
/// first <see cref="FeedbackServiceRatingStore.EnsureAsync"/> and re-hydrates it on a
/// local miss. These tests exercise the EnsureAsync mirror + hydrate directly (no
/// upstream feedback-service round trip is needed for the seed path), simulating a
/// bounce with a fresh store instance that shares the SAME durable KV.
/// </summary>
public sealed class FeedbackServiceRatingStoreSeedDurabilityTests
{
    private static FeedbackServiceRatingStore NewStore(IIdempotencyStore durable) =>
        new(new ThrowingScopeFactory(), durable, NullLogger<FeedbackServiceRatingStore>.Instance);

    [Fact]
    public async Task Seed_Survives_A_Bounce_ColdInstance_Recovers_Original_Anchor()
    {
        var durable = new InMemoryIdempotencyStore(TimeProvider.System);
        var t0 = DateTimeOffset.UtcNow;

        // Instance A seeds the delivery (mirrors the anchor durably).
        var instanceA = NewStore(durable);
        var seededA = await instanceA.EnsureAsync("del-1", "client-1", "jeeber-1", t0, CancellationToken.None);
        seededA.ClientId.Should().Be("client-1");

        // Instance B is COLD (fresh in-memory seed map — a bounce) but shares the durable
        // KV. Calling EnsureAsync with DIFFERENT party ids must recover the ORIGINAL
        // anchor from the durable store, proving both (a) the seed persisted and (b) the
        // first-call anchor stability holds across a restart.
        var instanceB = NewStore(durable);
        var recovered = await instanceB.EnsureAsync(
            "del-1", "client-DIFFERENT", "jeeber-DIFFERENT", t0.AddHours(5), CancellationToken.None);

        recovered.ClientId.Should().Be("client-1", "the durable seed's original party map is recovered");
        recovered.JeeberId.Should().Be("jeeber-1");
        recovered.DeliveredAt.Should().Be(t0, "the original delivered-at anchor is preserved across the bounce");
    }

    [Fact]
    public async Task Seed_Not_Present_ColdInstance_Creates_Fresh()
    {
        var durable = new InMemoryIdempotencyStore(TimeProvider.System);
        var t0 = DateTimeOffset.UtcNow;

        var store = NewStore(durable);
        var seeded = await store.EnsureAsync("del-new", "client-9", "jeeber-9", t0, CancellationToken.None);

        seeded.DeliveryId.Should().Be("del-new");
        seeded.ClientId.Should().Be("client-9");
        seeded.JeeberId.Should().Be("jeeber-9");
        seeded.DeliveredAt.Should().Be(t0);

        // And it is now durably mirrored — a cold instance recovers it.
        var cold = NewStore(durable);
        var recovered = await cold.EnsureAsync(
            "del-new", "other", "other", t0.AddDays(1), CancellationToken.None);
        recovered.ClientId.Should().Be("client-9");
    }

    [Fact]
    public async Task Seed_Mirror_Degrades_When_Durable_Faults_NeverThrows()
    {
        // A durable-store blip must never turn EnsureAsync into a fault — the seed just
        // stays in-memory only (the pre-fix behaviour).
        var faulting = new FaultingIdempotencyStore();
        var store = NewStore(faulting);

        Func<Task> act = () => store.EnsureAsync("del-x", "c", "j", DateTimeOffset.UtcNow, CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // -----------------------------------------------------------------
    // fakes
    // -----------------------------------------------------------------

    /// <summary>The EnsureAsync seed path never resolves a scope; guard that invariant.</summary>
    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("EnsureAsync must not resolve the feedback-service scope.");
    }

    private sealed class FaultingIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
            => throw new InvalidOperationException("state-service unavailable");

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => throw new InvalidOperationException("state-service unavailable");

        public Task<System.Collections.Generic.IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct)
            => throw new InvalidOperationException("state-service unavailable");
    }
}
