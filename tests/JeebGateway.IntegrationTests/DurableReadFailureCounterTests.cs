using System.Diagnostics.Metrics;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Durable;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// <c>durable.read_failures</c> — the counter whose absence made a six-week outage
/// invisible.
///
/// <para>WHAT WENT WRONG. <c>GET /state/idempotency/by-prefix</c> returned 404 on
/// EVERY call for six weeks. The gateway's degrade-don't-fail contract behaved exactly
/// as designed: caught the fault, logged a <c>warn</c>, served the in-memory fallback,
/// stayed 200. Writes had <c>durable.write_failures</c> the whole time. Reads had
/// nothing — so a TOTAL read outage and a perfectly healthy read path emitted the same
/// aggregate signal: silence.</para>
///
/// <para>WHAT THIS FILE PROVES, and why each half matters. Asserting only that a fault
/// increments the counter is half a test — a counter wired to fire on every read would
/// pass it and would be useless, because an alert on it could never distinguish an
/// outage from traffic. So every positive assertion here is paired with a negative
/// control on the SAME store: a healthy read that finds nothing must NOT increment. A
/// miss is an answer; a fault is not.</para>
///
/// <para>NO STUB OF THE UNIT UNDER TEST. Both instrumented types are the real
/// production classes (<see cref="StateServiceOfferRequestIndex"/>,
/// <see cref="DurableRequestsStore"/>), driven through their real public read methods.
/// What is substituted is the DEPENDENCY THAT FAILED IN PRODUCTION — the durable store
/// behind them — because a fault from that dependency IS the condition under test. Cf.
/// <c>BusinessOutcomeMetricsEndpointTests</c>, which states plainly that the durable
/// counter's decision point is "verified by adversarial code review" and asserts only
/// that the instrument is registered. That is the hole this file closes for the read
/// half.</para>
///
/// <para>NO ALERT THRESHOLD is asserted or implied here. What a healthy rate looks
/// like, and what should page, is an owner decision; this file proves only that the
/// signal exists and is specific.</para>
/// </summary>
[Collection(DurableReadFailureCollection.Name)]
public sealed class DurableReadFailureCounterTests
{
    private const string ReadFailures = "durable.read_failures";

    private sealed record Measurement(string Instrument, long Value, string? Store);

    /// <summary>
    /// Captures every long measurement on the business-outcome meter for the lifetime of
    /// the scope. Same mechanism as <c>BusinessOutcomeMetricsEndpointTests</c>: a
    /// MeterListener records deterministically at Add() time, with no dependence on the
    /// Prometheus exporter's collect timing.
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
                    l.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                string? store = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "store") store = tag.Value as string;
                }
                lock (_measurements)
                    _measurements.Add(new Measurement(instrument.Name, value, store));
            });
            _listener.Start();
        }

        public IReadOnlyList<Measurement> Snapshot()
        {
            lock (_measurements) return _measurements.ToList();
        }

        public IReadOnlyList<Measurement> ReadFailuresFor(string store)
            => Snapshot().Where(m => m.Instrument == ReadFailures && m.Store == store).ToList();

        public void Dispose() => _listener.Dispose();
    }

    // ---------------------------------------------------------------------------
    // 1. THE OUTAGE SITE: the by-prefix scan behind the jeeber's "my offers" list.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Prefix_scan_fault_is_counted_and_still_degrades_to_the_in_memory_set()
    {
        using var capture = new MeterCapture();

        var local = new InMemoryOfferRequestIndex();
        local.Record("offer-local", "request-1", "jeeber-1");

        var index = new StateServiceOfferRequestIndex(
            local,
            new FaultingIdempotencyStore(),
            NullLogger<StateServiceOfferRequestIndex>.Instance);

        var result = index.ListOfferIdsForJeeber("jeeber-1");

        // The degrade contract is unchanged — this must stay a served answer, not a throw.
        result.Should().BeEquivalentTo(new[] { "offer-local" });

        capture.ReadFailuresFor("state-service-offer-routing-reverse")
            .Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Fact]
    public void Healthy_prefix_scan_that_finds_nothing_is_NOT_counted()
    {
        // NEGATIVE CONTROL for the test above. Same call, same code path, healthy store
        // that legitimately holds no rows for this jeeber. If this ever increments, the
        // counter measures traffic rather than faults and no alert on it can mean
        // anything.
        using var capture = new MeterCapture();

        var local = new InMemoryOfferRequestIndex();
        local.Record("offer-local", "request-1", "jeeber-1");

        var index = new StateServiceOfferRequestIndex(
            local,
            new EmptyIdempotencyStore(),
            NullLogger<StateServiceOfferRequestIndex>.Instance);

        index.ListOfferIdsForJeeber("jeeber-1").Should().BeEquivalentTo(new[] { "offer-local" });

        capture.Snapshot().Where(m => m.Instrument == ReadFailures).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // 2. The forward pairing read — a fault here is served to the caller as
    //    "unknown offer", i.e. it is indistinguishable from a genuine miss at the 404.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Forward_pairing_read_fault_is_counted_and_still_resolves_as_unknown()
    {
        using var capture = new MeterCapture();

        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(),
            new FaultingIdempotencyStore(),
            NullLogger<StateServiceOfferRequestIndex>.Instance);

        index.ResolveRequestId("offer-not-in-local-cache").Should().BeNull();

        capture.ReadFailuresFor("state-service-offer-routing")
            .Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Fact]
    public void Healthy_forward_pairing_read_that_finds_nothing_is_NOT_counted()
    {
        using var capture = new MeterCapture();

        var index = new StateServiceOfferRequestIndex(
            new InMemoryOfferRequestIndex(),
            new EmptyIdempotencyStore(),
            NullLogger<StateServiceOfferRequestIndex>.Instance);

        index.ResolveRequestId("offer-not-in-local-cache").Should().BeNull();

        capture.Snapshot().Where(m => m.Instrument == ReadFailures).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // 3. The Postgres owner-list mirror — the other family of silent read fallbacks.
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Owner_list_mirror_fault_is_counted_and_still_returns_the_in_memory_rows()
    {
        using var capture = new MeterCapture();

        var store = BuildDurableStore(new FaultingRequestsMirror(), out var inner);
        var created = await inner.TryCreateWithLimitAsync(ValidInput("client-1"), limit: 10, CancellationToken.None);

        var rows = await store.ListForClientAsync("client-1", CancellationToken.None);

        rows.Should().ContainSingle().Which.Id.Should().Be(created.Id);

        capture.ReadFailuresFor("postgres-requests-owner-list")
            .Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Fact]
    public async Task Healthy_owner_list_mirror_with_no_rows_is_NOT_counted()
    {
        using var capture = new MeterCapture();

        var store = BuildDurableStore(new EmptyRequestsMirror(), out var inner);
        await inner.TryCreateWithLimitAsync(ValidInput("client-1"), limit: 10, CancellationToken.None);

        (await store.ListForClientAsync("client-1", CancellationToken.None)).Should().HaveCount(1);

        capture.Snapshot().Where(m => m.Instrument == ReadFailures).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // 4. Registration — the read counter is its own instrument, not a relabelled write.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Read_and_write_failure_counters_are_distinct_registered_instruments()
    {
        BusinessOutcomeTelemetry.DurableReadFailures.Name.Should().Be("durable.read_failures");
        BusinessOutcomeTelemetry.DurableWriteFailures.Name.Should().Be("durable.write_failures");
        BusinessOutcomeTelemetry.DurableReadFailures.Meter.Name
            .Should().Be(BusinessOutcomeTelemetry.MeterName,
                "both halves must land on the meter Program.cs already exports, or the "
                + "counter exists and is still invisible");
    }

    // ---------------------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------------------

    private static DurableRequestsStore BuildDurableStore(
        IDurableRequestsMirror mirror, out InMemoryRequestsStore inner)
    {
        inner = new InMemoryRequestsStore(TimeProvider.System);
        return new DurableRequestsStore(
            inner,
            new ThrowingDeliveryClient(),
            new NoOpBundleRecorder(),
            new NoOpConversationProvisioner(),
            new NoOpBroadcastRecorder(),
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance,
            mirror);
    }

    private static CreateRequestInput ValidInput(string clientId) => new()
    {
        ClientId = clientId,
        Description = "deliver a package",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 25.2, Lng = 55.3 },
        DropoffLocation = new GeoPoint { Lat = 25.4, Lng = 55.5 },
    };

    /// <summary>The production fault: state-service answers, and the answer is an error.</summary>
    private sealed class FaultingIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
            => throw new HttpRequestException("state-service PUT /state/idempotency -> 404");

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => throw new HttpRequestException("state-service GET /state/idempotency -> 404");

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
            => throw new HttpRequestException("state-service GET /state/idempotency/by-prefix -> 404");
    }

    /// <summary>Healthy store, genuinely empty. The negative control's dependency.</summary>
    private sealed class EmptyIdempotencyStore : IIdempotencyStore
    {
        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
            => Task.FromResult(new IdempotencyOutcome
            {
                Inserted = true, StatusCode = statusCode, ResponseBodyJson = responseBodyJson,
            });

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct)
            => Task.FromResult<IdempotencyOutcome?>(null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(string prefix, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(Array.Empty<IdempotencyOutcome>());
    }

    private abstract class RequestsMirrorBase : IDurableRequestsMirror
    {
        public Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct) => Task.CompletedTask;

        public Task MarkCancelledAsync(string requestId, string gwStatus, string? cancelledBy,
            string? cancellationReason, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> MarkExpiredAsync(string requestId, DateTimeOffset expiredAt, CancellationToken ct)
            => Task.FromResult(false);

        public Task UpdateLifecycleAsync(string requestId, string? gwStatus, string? gwJeeberId,
            decimal? gwAcceptedFee, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;

        public Task UpdateConversationIdAsync(string requestId, string conversationId, CancellationToken ct)
            => Task.CompletedTask;

        public abstract Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct);
        public abstract Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct);
        public abstract Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct);
        public abstract Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct);
        public abstract Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct);
    }

    private sealed class FaultingRequestsMirror : RequestsMirrorBase
    {
        private static Exception Fault() => new InvalidOperationException("gateway Postgres mirror unavailable");

        public override Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
            => throw Fault();
        public override Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
            => throw Fault();
        public override Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct) => throw Fault();
        public override Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct) => throw Fault();
        public override Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => throw Fault();
    }

    private sealed class EmptyRequestsMirror : RequestsMirrorBase
    {
        public override Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public override Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public override Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());
        public override Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
            => Task.FromResult<DeliveryRequest?>(null);
        public override Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => Task.FromResult<DeliveryRequest?>(null);
    }

    private sealed class NoOpBundleRecorder : ISagaBundleRecorder
    {
        public Task<SagaBundleRecordOutcome> RecordCreatedAsync(
            string sourceId, string tag, object state, CancellationToken ct)
            => Task.FromResult(SagaBundleRecordOutcome.Recorded);
    }

    private sealed class NoOpBroadcastRecorder : IBroadcastEventRecorder
    {
        public Task<BroadcastEventRecordOutcome> RecordBroadcastingAsync(
            string contextId, string phase, CancellationToken ct)
            => Task.FromResult(BroadcastEventRecordOutcome.Recorded);
    }

    private sealed class NoOpConversationProvisioner : IConversationProvisioner
    {
        public Task<string?> CreateBroadcastingConversationAsync(
            string requestId, string clientId, CancellationToken ct)
            => Task.FromResult<string?>(null);

    }

    /// <summary>
    /// The delivery client is a constructor dependency of <see cref="DurableRequestsStore"/>
    /// but is NOT on the owner-list read path (<c>ListForClientAsync</c> touches only the
    /// inner store and the mirror). Every member throws on purpose: if the read path ever
    /// starts calling upstream, these tests must fail loudly rather than quietly widen.
    /// </summary>
    private sealed class ThrowingDeliveryClient : IDeliveryServiceClient
    {
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

        private static Exception Unexpected([System.Runtime.CompilerServices.CallerMemberName] string member = "")
            => new NotSupportedException(
                $"IDeliveryServiceClient.{member} is not on the durable owner-list READ path; "
                + "if this fires, the path under test changed shape.");

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw Unexpected();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw Unexpected();
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw Unexpected();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw Unexpected();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw Unexpected();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw Unexpected();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw Unexpected();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw Unexpected();
    }
}
