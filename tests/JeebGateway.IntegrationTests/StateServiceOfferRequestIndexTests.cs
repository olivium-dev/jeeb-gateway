using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Observability;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 (A3/N9) — the DURABLE offer→request routing index, pinned against the ctor that is
/// actually live: <c>(InMemoryOfferRequestIndex local, IIdempotencyStore durable, ILogger)</c>.
///
/// <para><b>Provenance.</b> <c>94c2b63</c> ("verbatim rescue of interrupted ops-gateway
/// residue") rewrote BOTH this class and its test into an owner-only, FAIL-CLOSED shape:
/// a single-arg <c>StateServiceOfferRequestIndex(IExternalIdempotencyStore owner)</c> with no
/// local cache, whose <c>Record</c>/<c>Resolve</c> THREW when the owner was down. <c>231736d</c>
/// ("restore StateServiceOfferRequestIndex — gateway could not boot") reverted the production
/// class to the local+durable, DEGRADE-DON'T-FAIL design and left the test behind, which is
/// the CS7036/CS1503 this file repairs.</para>
///
/// <para><b>What could not be carried over.</b> The residue test's
/// <c>Owner_failure_is_not_hidden_by_a_local_fallback</c> asserted the exact OPPOSITE of the
/// restored contract — live code catches every durable fault, counts it, and serves the
/// in-memory answer. That assertion cannot be made true without changing production, so it is
/// NOT reproduced here. What replaces it is
/// <see cref="Owner_failure_is_absorbed_but_is_never_silent"/>: the fallback is allowed, but it
/// MUST emit <c>durable.read_failures</c>. That is the six-week-outage shape —
/// <c>GET /state/idempotency/by-prefix</c> 404'd on every call while this class quietly served
/// the in-memory set — and with <c>DurableReadFailureCounterTests</c> currently
/// <c>&lt;Compile Remove&gt;</c>d from the csproj, this file is the only live assertion of that
/// signal.</para>
/// </summary>
// Serialised with every other class that can move DurableReadFailures: the counter is static
// and its only tag is a bounded store literal. See DurableReadFailureCollection.
[Collection(DurableReadFailureCollection.Name)]
public sealed class StateServiceOfferRequestIndexTests
{
    private const string ReadFailures = "durable.read_failures";

    private static StateServiceOfferRequestIndex Index(
        IIdempotencyStore durable, InMemoryOfferRequestIndex? local = null)
        => new(local ?? new InMemoryOfferRequestIndex(), durable,
            NullLogger<StateServiceOfferRequestIndex>.Instance);

    // ------------------------------------------------------------------
    // 1. The pairing reaches external owner state (forward AND reverse).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Record_mirrors_the_pairing_into_external_owner_state()
    {
        var owner = new FakeOwnerStore();

        Index(owner).Record("off-1", "req-1", "jeeber-1");
        // F2: a Record carrying a jeeberId writes a FORWARD and a REVERSE KV row.
        await owner.WaitForWritesAsync(2);

        owner.Get("offer-routing:off-1")
            .Should().NotBeNull("the forward pairing is mirrored under the namespaced key");
        owner.Get("offer-routing-jeeber:jeeber-1:off-1")
            .Should().Be("off-1", "the reverse row's body is the offerId verbatim");
    }

    [Fact]
    public async Task Reverse_and_forward_key_namespaces_never_cross_match()
    {
        // The forward GET is 'offer-routing:' and the reverse prefix-scan is
        // 'offer-routing-jeeber:'; a scan of one must never pick up the other's rows.
        var owner = new FakeOwnerStore();
        Index(owner).Record("off-ns", "req-ns", "jeeber-ns");
        await owner.WaitForWritesAsync(2);

        owner.KeysWithPrefix("offer-routing-jeeber:")
            .Should().ContainSingle().Which.Should().Be("offer-routing-jeeber:jeeber-ns:off-ns");
        owner.KeysWithPrefix("offer-routing:")
            .Should().ContainSingle().Which.Should().Be("offer-routing:off-ns");
    }

    // ------------------------------------------------------------------
    // 2. A cold instance recovers everything from owner state alone.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Cold_instance_reads_the_same_owner_records()
    {
        var owner = new FakeOwnerStore();
        Index(owner).Record("off-2", "req-2", "jeeber-2");
        await owner.WaitForWritesAsync(2);

        // A fresh local cache is a gateway bounce / a different replica. Owner state is
        // the ONLY thing shared, so every value below came back out of it.
        var cold = Index(owner);

        cold.ResolveRequestId("off-2").Should().Be("req-2");
        cold.ResolveJeeberId("off-2").Should().Be("jeeber-2");
    }

    [Fact]
    public async Task Cold_instance_recovers_the_jeebers_own_offers_from_owner_state()
    {
        var owner = new FakeOwnerStore();
        var warm = Index(owner);
        warm.Record("off-a1", "req-a1", "jeeber-X");
        warm.Record("off-a2", "req-a2", "jeeber-X");
        warm.Record("off-b1", "req-b1", "jeeber-Y");
        await owner.WaitForWritesAsync(6);

        var cold = Index(owner);

        cold.ListOfferIdsForJeeber("jeeber-X")
            .Should().BeEquivalentTo(new[] { "off-a1", "off-a2" },
                "the cold replica recovers the jeeber's bids from the durable reverse index");
        cold.ListOfferIdsForJeeber("jeeber-Y").Should().BeEquivalentTo(new[] { "off-b1" });
        cold.ListOfferIdsForJeeber("jeeber-none")
            .Should().BeEmpty("a jeeber that never bid has no reverse rows");
    }

    [Fact]
    public async Task List_unions_local_and_owner_rows_without_duplicating_an_offer()
    {
        var owner = new FakeOwnerStore();
        var index = Index(owner);
        index.Record("off-dup", "req-dup", "jeeber-Z");
        await owner.WaitForWritesAsync(2);

        index.ListOfferIdsForJeeber("jeeber-Z").Should().ContainSingle().Which.Should().Be("off-dup");
    }

    [Fact]
    public void Resolve_of_an_unknown_offer_returns_null_phantom_offer_contract()
    {
        var index = Index(new EmptyOwnerStore());

        index.ResolveRequestId("never-seen").Should().BeNull();
        index.ResolveJeeberId("never-seen").Should().BeNull();
    }

    // ------------------------------------------------------------------
    // 3. Owner failure: absorbed by design — but it must not be silent.
    // ------------------------------------------------------------------

    [Fact]
    public void Owner_failure_degrades_to_the_local_cache_and_never_throws()
    {
        var local = new InMemoryOfferRequestIndex();
        var index = Index(new FaultingOwnerStore(), local);

        // A mirror fault must not throw into the offer-submit 201 path...
        index.Invoking(i => i.Record("off-3", "req-3", "jeeber-3")).Should().NotThrow();
        // ...and the pairing still resolves within this instance.
        index.ResolveRequestId("off-3").Should().Be("req-3");
        index.ListOfferIdsForJeeber("jeeber-3").Should().BeEquivalentTo(new[] { "off-3" });

        // A read fault on a local MISS degrades to null (phantom-offer 404), never throws.
        index.Invoking(i => i.ResolveRequestId("off-cold-miss")).Should().NotThrow();
        index.ResolveRequestId("off-cold-miss").Should().BeNull();
    }

    [Fact]
    public void Owner_failure_is_absorbed_but_is_never_silent()
    {
        // The residue test demanded a THROW here. Live code cannot throw — 231736d restored
        // degrade-don't-fail on purpose. So the falsifiable claim is: the fallback is counted.
        using var capture = new MeterCapture();
        var index = Index(new FaultingOwnerStore());

        index.ListOfferIdsForJeeber("jeeber-down")
            .Should().BeEmpty("a cold instance with a dead owner has nothing to fall back to");
        index.ResolveRequestId("off-down")
            .Should().BeNull("a fault is served as 'unknown offer', indistinguishable at the 404");

        // Without these two counters an owner outage and a healthy empty read look identical.
        capture.ReadFailuresFor("state-service-offer-routing-reverse")
            .Should().ContainSingle().Which.Should().Be(1);
        capture.ReadFailuresFor("state-service-offer-routing")
            .Should().ContainSingle().Which.Should().Be(1);
    }

    [Fact]
    public void Healthy_owner_that_simply_finds_nothing_is_NOT_counted_as_a_failure()
    {
        // Negative control. A counter that fires on every read measures traffic, not faults,
        // and no alert built on it could tell an outage from a quiet jeeber.
        using var capture = new MeterCapture();
        var index = Index(new EmptyOwnerStore());

        index.ListOfferIdsForJeeber("jeeber-quiet").Should().BeEmpty();
        index.ResolveRequestId("off-quiet").Should().BeNull();

        capture.Snapshot().Where(m => m.Instrument == ReadFailures).Should().BeEmpty();
    }

    // ------------------------------------------------------------------
    // fixtures
    // ------------------------------------------------------------------

    private sealed record Measurement(string Instrument, long Value, string? Store);

    /// <summary>
    /// Records at Add() time via a MeterListener, so nothing depends on the Prometheus
    /// exporter's collect timing.
    /// </summary>
    private sealed class MeterCapture : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly List<Measurement> _measurements = new();

        public MeterCapture()
        {
            _listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? store = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "store")
                    {
                        store = tag.Value as string;
                    }
                }
                lock (_measurements)
                {
                    _measurements.Add(new Measurement(instrument.Name, value, store));
                }
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Snapshot()
        {
            lock (_measurements)
            {
                return _measurements.ToList();
            }
        }

        public IReadOnlyList<long> ReadFailuresFor(string store)
            => Snapshot().Where(m => m.Instrument == ReadFailures && m.Store == store)
                .Select(m => m.Value).ToList();

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Stands in for jeeb-state-service's idempotency KV: insert-once PutOrGet, GET-by-key
    /// and the by-prefix scan the reverse index needs.
    /// </summary>
    private sealed class FakeOwnerStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _kv = new(StringComparer.Ordinal);
        private int _writes;

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            var stored = _kv.GetOrAdd(key, responseBodyJson);
            Interlocked.Increment(ref _writes);
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = ReferenceEquals(stored, responseBodyJson),
                StatusCode = statusCode,
                ResponseBodyJson = stored,
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult(_kv.TryGetValue(key, out var body)
                ? new IdempotencyOutcome { Inserted = false, StatusCode = 200, ResponseBodyJson = body }
                : null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(_kv
                .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(pair => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = pair.Value,
                })
                .ToList());

        public string? Get(string key) => _kv.TryGetValue(key, out var value) ? value : null;

        public IReadOnlyList<string> KeysWithPrefix(string prefix)
            => _kv.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        /// <summary>Awaits the fire-and-forget mirror so assertions are not racy.</summary>
        public async Task WaitForWritesAsync(int expected)
        {
            for (var i = 0; i < 200 && Volatile.Read(ref _writes) < expected; i++)
            {
                await Task.Delay(10);
            }

            Volatile.Read(ref _writes).Should().BeGreaterThanOrEqualTo(expected,
                "the durable mirror never landed, so nothing below would be a real read");
        }
    }

    /// <summary>Healthy owner that genuinely holds nothing — the negative control.</summary>
    private sealed class EmptyOwnerStore : IIdempotencyStore
    {
        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
            => Task.FromResult(new IdempotencyOutcome
            {
                Inserted = true,
                StatusCode = statusCode,
                ResponseBodyJson = responseBodyJson,
            });

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult<IdempotencyOutcome?>(null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(Array.Empty<IdempotencyOutcome>());
    }

    /// <summary>The production fault: state-service answers, and the answer is an error.</summary>
    private sealed class FaultingOwnerStore : IIdempotencyStore
    {
        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
            => throw new HttpRequestException("state-service PUT /state/idempotency -> 404");

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => throw new HttpRequestException("state-service GET /state/idempotency -> 404");

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct)
            => throw new HttpRequestException("state-service GET /state/idempotency/by-prefix -> 404");
    }
}
