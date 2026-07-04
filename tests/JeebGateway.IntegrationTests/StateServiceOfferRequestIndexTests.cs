using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 (A3/N9) — the DURABLE offer→request routing index. These tests pin the
/// bounce/replica-survivable behaviour that fixes the spurious offer edit/accept 404:
/// the pairing is mirrored into jeeb-state-service's idempotency KV at submit time, a
/// COLD instance (empty local cache, simulating a bounce or a different replica)
/// re-hydrates the pairing from the durable store on resolve, and a durable-store fault
/// degrades to the in-memory result (never throws into the offer hot path).
/// </summary>
public sealed class StateServiceOfferRequestIndexTests
{
    [Fact]
    public async Task Record_Mirrors_Pairing_To_DurableStore()
    {
        var durable = new FakeIdempotencyStore();
        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);

        index.Record("off-1", "req-1", "jeeber-1");
        // F2: a Record with a jeeberId now mirrors a FORWARD (offer→request) and a
        // REVERSE (jeeber→offer) KV row — 2 writes.
        await durable.WaitForWritesAsync(2);

        durable.Get("offer-routing:off-1").Should().NotBeNull("the pairing is mirrored under the namespaced key");
    }

    [Fact]
    public async Task Resolve_On_ColdInstance_ReHydrates_From_DurableStore()
    {
        // Instance A records (and mirrors).
        var durable = new FakeIdempotencyStore();
        var instanceA = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);
        instanceA.Record("off-2", "req-2", "jeeber-2");
        // F2: forward + reverse mirror = 2 writes.
        await durable.WaitForWritesAsync(2);

        // Instance B is COLD (fresh in-memory cache — a bounce / different replica) but
        // shares the SAME durable store. It must resolve the pairing from the durable mirror.
        var instanceB = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);

        instanceB.ResolveRequestId("off-2").Should().Be("req-2");
        instanceB.ResolveJeeberId("off-2").Should().Be("jeeber-2");
    }

    [Fact]
    public void Resolve_Unknown_Offer_Returns_Null_PhantomOfferContract()
    {
        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), new FakeIdempotencyStore(),
            NullLogger<StateServiceOfferRequestIndex>.Instance);

        index.ResolveRequestId("never-seen").Should().BeNull();
        index.ResolveJeeberId("never-seen").Should().BeNull();
    }

    [Fact]
    public void Resolve_When_DurableStore_Faults_DegradesTo_LocalCache_NeverThrows()
    {
        var faulting = new FaultingIdempotencyStore();
        var local = new InMemoryOfferRequestIndex();
        var index = new StateServiceOfferRequestIndex(
            local, faulting, NullLogger<StateServiceOfferRequestIndex>.Instance);

        // A mirror fault on Record must not throw into the offer hot path...
        index.Invoking(i => i.Record("off-3", "req-3", "jeeber-3")).Should().NotThrow();
        // ...and the local cache still resolves it within the instance.
        index.ResolveRequestId("off-3").Should().Be("req-3");

        // A durable read fault on a local miss degrades to null (phantom-offer 404), never throws.
        index.Invoking(i => i.ResolveRequestId("off-cold-miss")).Should().NotThrow();
        index.ResolveRequestId("off-cold-miss").Should().BeNull();
    }

    [Fact]
    public async Task ListOfferIdsForJeeber_On_ColdInstance_ReHydrates_From_DurableReverseIndex()
    {
        // F2: the jeeber → offerIds reverse index must survive a bounce. Instance A
        // records two offers for jeeber-X and one for jeeber-Y (each Record mirrors a
        // forward AND a reverse KV row = 2 writes), so 3 records => 6 durable writes.
        var durable = new FakeIdempotencyStore();
        var instanceA = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);
        instanceA.Record("off-a1", "req-a1", "jeeber-X");
        instanceA.Record("off-a2", "req-a2", "jeeber-X");
        instanceA.Record("off-b1", "req-b1", "jeeber-Y");
        await durable.WaitForWritesAsync(6);

        // Instance B is COLD (empty local cache — a bounce / different replica) but
        // shares the SAME durable store. Its my-offers list must be recovered durably.
        var instanceB = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);

        instanceB.ListOfferIdsForJeeber("jeeber-X")
            .Should().BeEquivalentTo(new[] { "off-a1", "off-a2" },
                "the cold replica recovers the jeeber's own offers from the durable reverse index");
        instanceB.ListOfferIdsForJeeber("jeeber-Y")
            .Should().BeEquivalentTo(new[] { "off-b1" });
        instanceB.ListOfferIdsForJeeber("jeeber-none")
            .Should().BeEmpty("a jeeber that never bid has no durable reverse rows");
    }

    [Fact]
    public async Task ListOfferIdsForJeeber_Unions_Local_And_Durable_WithoutDuplicates()
    {
        // A warm instance that also has the durable rows must not double-count an offer
        // present in both its local cache and the durable reverse index.
        var durable = new FakeIdempotencyStore();
        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), durable, NullLogger<StateServiceOfferRequestIndex>.Instance);
        index.Record("off-dup", "req-dup", "jeeber-Z");
        await durable.WaitForWritesAsync(2);

        index.ListOfferIdsForJeeber("jeeber-Z")
            .Should().ContainSingle().Which.Should().Be("off-dup");
    }

    [Fact]
    public void ListOfferIdsForJeeber_When_DurableStore_Faults_DegradesTo_LocalCache_NeverThrows()
    {
        var faulting = new FaultingIdempotencyStore();
        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(), faulting, NullLogger<StateServiceOfferRequestIndex>.Instance);

        // A mirror fault on Record must not throw, and the local reverse lookup still works.
        index.Invoking(i => i.Record("off-f1", "req-f1", "jeeber-F")).Should().NotThrow();
        index.Invoking(i => i.ListOfferIdsForJeeber("jeeber-F")).Should().NotThrow();
        index.ListOfferIdsForJeeber("jeeber-F").Should().BeEquivalentTo(new[] { "off-f1" });
    }

    // -----------------------------------------------------------------
    // fakes
    // -----------------------------------------------------------------

    private sealed class FakeIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);
        private int _writes;

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            var stored = _kv.GetOrAdd(key, responseBodyJson);
            Interlocked.Increment(ref _writes);
            var inserted = ReferenceEquals(stored, responseBodyJson);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = statusCode,
                ResponseBodyJson = stored,
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_kv.TryGetValue(key, out var body)
                ? new IdempotencyOutcome { Inserted = false, StatusCode = 200, ResponseBodyJson = body }
                : null);

        public Task<System.Collections.Generic.IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct)
        {
            System.Collections.Generic.IReadOnlyList<IdempotencyOutcome> rows = System.Linq.Enumerable.ToList(
                System.Linq.Enumerable.Select(
                    System.Linq.Enumerable.Where(_kv, kvp => kvp.Key.StartsWith(prefix, StringComparison.Ordinal)),
                    kvp => new IdempotencyOutcome { Inserted = false, StatusCode = 200, ResponseBodyJson = kvp.Value }));
            return Task.FromResult(rows);
        }

        public string? Get(string key) => _kv.TryGetValue(key, out var v) ? v : null;

        /// <summary>Awaits the fire-and-forget mirror so the assertion is not racy.</summary>
        public async Task WaitForWritesAsync(int expected)
        {
            for (var i = 0; i < 100 && Volatile.Read(ref _writes) < expected; i++)
            {
                await Task.Delay(10);
            }
        }
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
