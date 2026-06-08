using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Durable;
using JeebGateway.Tiers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// SPINE-FOUNDATION / ADR-006: unit coverage for the stateless create path.
/// Proves the durable decorator (a) keeps ONE id stable across the request, the
/// seeded delivery row, and the saga bundle (the silent re-404 guard E5);
/// (b) preserves the BR-9 cap and seeds NO upstream row on a cap rejection
/// (E7 — no relaxed asserts, no orphan); (c) degrades — not fails — when the
/// saga ledger is unavailable; and (d) delegates every non-create method to the
/// in-memory inner model.
/// </summary>
public sealed class DurableRequestsStoreTests
{
    private static readonly TimeProvider Clock = TimeProvider.System;

    private static DurableRequestsStore Build(
        out InMemoryRequestsStore inner,
        out RecordingDeliveryClient delivery,
        out RecordingBundleRecorder bundles,
        SagaBundleRecordOutcome bundleOutcome = SagaBundleRecordOutcome.Recorded)
    {
        inner = new InMemoryRequestsStore(Clock);
        delivery = new RecordingDeliveryClient();
        bundles = new RecordingBundleRecorder(bundleOutcome);
        var options = Options.Create(new DurableRequestsOptions { Enabled = true });
        return new DurableRequestsStore(
            inner, delivery, bundles, options,
            NullLogger<DurableRequestsStore>.Instance);
    }

    private static CreateRequestInput ValidInput(string clientId = "client-1") => new()
    {
        ClientId = clientId,
        Description = "deliver a package",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 25.2, Lng = 55.3 },
        DropoffLocation = new GeoPoint { Lat = 25.4, Lng = 55.5 },
    };

    [Fact]
    public async Task Create_seeds_delivery_row_and_bundle_with_one_stable_id()
    {
        var store = Build(out var inner, out var delivery, out var bundles);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // E5 stability gate: request_id == delivery row id == bundle sourceId.
        created.Id.Should().NotBeNullOrWhiteSpace();
        delivery.CreatedRows.Should().ContainSingle();
        delivery.CreatedRows[0].Id.Should().Be(created.Id);
        delivery.CreatedRows[0].TierId.Should().Be("flash");
        delivery.CreatedRows[0].PickupLat.Should().Be(25.2);
        bundles.Recorded.Should().ContainSingle();
        bundles.Recorded[0].SourceId.Should().Be(created.Id);
        bundles.Recorded[0].Tag.Should().Be("delivery_saga_v1");

        // The inner in-memory model is the read source — same id resolves there.
        var roundTrip = await store.GetAsync(created.Id, CancellationToken.None);
        roundTrip.Should().NotBeNull();
        roundTrip!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task Create_honours_caller_supplied_id_across_row_and_bundle()
    {
        var store = Build(out _, out var delivery, out var bundles);
        var input = ValidInput();
        var withId = new CreateRequestInput
        {
            ClientId = input.ClientId,
            Description = input.Description,
            TierId = input.TierId,
            PickupLocation = input.PickupLocation,
            DropoffLocation = input.DropoffLocation,
            Id = "voice-anchor-42",
        };

        var created = await store.TryCreateWithLimitAsync(withId, limit: 3, CancellationToken.None);

        created.Id.Should().Be("voice-anchor-42");
        delivery.CreatedRows[0].Id.Should().Be("voice-anchor-42");
        bundles.Recorded[0].SourceId.Should().Be("voice-anchor-42");
    }

    [Fact]
    public async Task Cap_rejection_throws_and_seeds_no_upstream_row_or_bundle()
    {
        var store = Build(out _, out var delivery, out var bundles);

        // Fill the cap (limit 1) then attempt a second create.
        await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);
        delivery.CreatedRows.Should().ContainSingle();

        var act = async () => await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);

        await act.Should().ThrowAsync<TooManyActiveRequestsException>();

        // E7 / no-orphan: the rejected create added NO delivery row and NO bundle.
        delivery.CreatedRows.Should().ContainSingle("the cap rejection must not seed a second row");
        bundles.Recorded.Should().ContainSingle("the cap rejection must not record a second bundle");
    }

    [Fact]
    public async Task Create_succeeds_when_saga_ledger_unavailable()
    {
        var store = Build(out _, out var delivery, out var bundles,
            bundleOutcome: SagaBundleRecordOutcome.Unavailable);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // The delivery row (the matching-resolve hard dependency) is still seeded;
        // a ledger blip degrades the saga trail but does not fail the create.
        created.Id.Should().NotBeNullOrWhiteSpace();
        delivery.CreatedRows.Should().ContainSingle();
        bundles.Recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_throws_when_delivery_row_id_mismatches_request_id()
    {
        var inner = new InMemoryRequestsStore(Clock);
        var delivery = new RecordingDeliveryClient { EchoMismatchedId = "WRONG-ID" };
        var bundles = new RecordingBundleRecorder(SagaBundleRecordOutcome.Recorded);
        var store = new DurableRequestsStore(
            inner, delivery, bundles,
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance);

        var act = async () => await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // A row-id mismatch would silently re-introduce the matching 404 — it must throw.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Non_create_methods_delegate_to_inner()
    {
        var store = Build(out var inner, out var delivery, out _);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);
        delivery.CreatedRows.Should().ContainSingle();

        // List/Count/Cancel all read from the inner model with no extra upstream writes.
        var list = await store.ListForClientAsync("client-1", CancellationToken.None);
        list.Should().ContainSingle(r => r.Id == created.Id);

        var active = await store.CountActiveForClientAsync("client-1", CancellationToken.None);
        active.Should().Be(1);

        var cancelled = await store.SetStatusAsync(created.Id, RequestStatus.Cancelled, CancellationToken.None);
        cancelled.Should().BeTrue();

        // Delegation must not have triggered additional delivery-row seeds.
        delivery.CreatedRows.Should().ContainSingle();
    }

    // -- fakes ---------------------------------------------------------------

    private sealed class RecordingDeliveryClient : NotImplementedDeliveryClient
    {
        public List<CreateDeliveryRowUpstream> CreatedRows { get; } = new();
        public string? EchoMismatchedId { get; init; }

        public override Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            CreatedRows.Add(body);
            return Task.FromResult(new DeliveryRowUpstream
            {
                Id = EchoMismatchedId ?? body.Id,
                TenantId = body.TenantId,
                Status = "Ordered",
            });
        }
    }

    private sealed class RecordingBundleRecorder : ISagaBundleRecorder
    {
        private readonly SagaBundleRecordOutcome _outcome;
        public List<(string SourceId, string Tag)> Recorded { get; } = new();

        public RecordingBundleRecorder(SagaBundleRecordOutcome outcome) => _outcome = outcome;

        public Task<SagaBundleRecordOutcome> RecordCreatedAsync(string sourceId, string tag, object state, CancellationToken ct)
        {
            Recorded.Add((sourceId, tag));
            return Task.FromResult(_outcome);
        }
    }

    /// <summary>
    /// Minimal <see cref="IDeliveryServiceClient"/> base that throws on every
    /// member the durable create path must NOT touch — so the test fails loudly
    /// if the decorator ever calls an unexpected upstream method.
    /// </summary>
    private abstract class NotImplementedDeliveryClient : IDeliveryServiceClient
    {
        public virtual Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotImplementedException();
    }
}
