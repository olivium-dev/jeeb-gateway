using FluentAssertions;
using JeebGateway.Conversations;
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
        => Build(out inner, out delivery, out bundles, out _, out _, bundleOutcome);

    private static DurableRequestsStore Build(
        out InMemoryRequestsStore inner,
        out RecordingDeliveryClient delivery,
        out RecordingBundleRecorder bundles,
        out RecordingConversationProvisioner conversations,
        SagaBundleRecordOutcome bundleOutcome = SagaBundleRecordOutcome.Recorded,
        string? conversationId = null)
        => Build(out inner, out delivery, out bundles, out conversations, out _, bundleOutcome, conversationId);

    private static DurableRequestsStore Build(
        out InMemoryRequestsStore inner,
        out RecordingDeliveryClient delivery,
        out RecordingBundleRecorder bundles,
        out RecordingConversationProvisioner conversations,
        out RecordingBroadcastEventRecorder broadcasts,
        SagaBundleRecordOutcome bundleOutcome = SagaBundleRecordOutcome.Recorded,
        string? conversationId = null,
        BroadcastEventRecordOutcome broadcastOutcome = BroadcastEventRecordOutcome.Recorded)
    {
        inner = new InMemoryRequestsStore(Clock);
        delivery = new RecordingDeliveryClient();
        bundles = new RecordingBundleRecorder(bundleOutcome);
        conversations = new RecordingConversationProvisioner(conversationId);
        broadcasts = new RecordingBroadcastEventRecorder(broadcastOutcome);
        var options = Options.Create(new DurableRequestsOptions { Enabled = true });
        return new DurableRequestsStore(
            inner, delivery, bundles, conversations, broadcasts, options,
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
        var conversations = new RecordingConversationProvisioner(null);
        var broadcasts = new RecordingBroadcastEventRecorder(BroadcastEventRecordOutcome.Recorded);
        var store = new DurableRequestsStore(
            inner, delivery, bundles, conversations, broadcasts,
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance);

        var act = async () => await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // A row-id mismatch would silently re-introduce the matching 404 — it must throw.
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Create_auto_creates_broadcasting_conversation_and_stamps_id()
    {
        // JEB-50 (S05 H7 / H9b): the durable create path auto-creates the
        // broadcasting conversation and surfaces its id on the row so the create
        // DTO + read-back carry conversationId.
        var store = Build(out _, out _, out var bundles, out var conversations,
            conversationId: "conv-123");

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // The conversation was provisioned exactly once, for THIS order, by the
        // ordering client.
        conversations.Calls.Should().ContainSingle();
        conversations.Calls[0].RequestId.Should().Be(created.Id);
        conversations.Calls[0].ClientId.Should().Be("client-1");

        // The id is stamped onto the created row (surfaced as conversationId).
        created.ConversationId.Should().Be("conv-123");

        // ...and persists on read-back (GET /requests/{id} → conversationId).
        var roundTrip = await store.GetAsync(created.Id, CancellationToken.None);
        roundTrip!.ConversationId.Should().Be("conv-123");

        // ...and is recorded in the saga audit trail.
        bundles.Recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_succeeds_when_conversation_provisioner_returns_null()
    {
        // A chat blip (or the auto-create flag OFF) degrades to null — the order
        // create STILL succeeds; ConversationId is simply left unset. A chat
        // outage must never fail POST /requests.
        var store = Build(out _, out var delivery, out _, out var conversations,
            conversationId: null);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        created.Id.Should().NotBeNullOrWhiteSpace();
        created.ConversationId.Should().BeNull();
        // The delivery row (the hard dependency) is still seeded.
        delivery.CreatedRows.Should().ContainSingle();
        conversations.Calls.Should().ContainSingle();
    }

    [Fact]
    public async Task Create_logs_broadcast_event_when_conversation_is_provisioned()
    {
        // JEB-50 (S05 H9b): when the broadcasting conversation is created, the
        // gateway LOGS the broadcast event to the jeeb-state bundler — keyed by
        // the conversation id (contextId) and carrying phase "broadcasting".
        var store = Build(out _, out _, out _, out var conversations, out var broadcasts,
            conversationId: "conv-123");

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        created.ConversationId.Should().Be("conv-123");
        conversations.Calls.Should().ContainSingle();

        // Exactly one broadcast-log row, keyed by the conversation id, phase=broadcasting.
        broadcasts.Recorded.Should().ContainSingle();
        broadcasts.Recorded[0].ContextId.Should().Be("conv-123");
        broadcasts.Recorded[0].Phase.Should().Be("broadcasting");
    }

    [Fact]
    public async Task Create_does_not_log_broadcast_when_no_conversation_is_provisioned()
    {
        // No broadcasting conversation (chat blip / flag OFF → null id) means the
        // order never entered the broadcasting phase, so NO broadcast-log row is
        // appended. The log mirrors the real chat-side phase, not a fiction.
        var store = Build(out _, out _, out _, out var conversations, out var broadcasts,
            conversationId: null);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        created.ConversationId.Should().BeNull();
        conversations.Calls.Should().ContainSingle();
        broadcasts.Recorded.Should().BeEmpty("no broadcasting conversation → no broadcast log");
    }

    [Fact]
    public async Task Create_succeeds_when_broadcast_log_unavailable()
    {
        // A state-service blip on the broadcast LOG must NOT fail the order create
        // (degrade-don't-fail): the conversation is still stamped and the create
        // still succeeds — the log is the durable audit trail, not a hard dep.
        var store = Build(out _, out var delivery, out _, out var conversations, out var broadcasts,
            conversationId: "conv-123",
            broadcastOutcome: BroadcastEventRecordOutcome.Unavailable);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        created.Id.Should().NotBeNullOrWhiteSpace();
        created.ConversationId.Should().Be("conv-123");
        delivery.CreatedRows.Should().ContainSingle();
        // The recorder WAS invoked (and degraded) — the create did not throw.
        broadcasts.Recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task Cap_rejection_does_not_log_a_broadcast_event()
    {
        // The broadcast log is appended only AFTER a successful insert + provision.
        // A BR-9 cap rejection throws before any side-effect, so no orphan log row.
        var store = Build(out _, out _, out _, out _, out var broadcasts,
            conversationId: "conv-123");

        await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);
        broadcasts.Recorded.Should().ContainSingle();

        var act = async () => await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);
        await act.Should().ThrowAsync<TooManyActiveRequestsException>();

        broadcasts.Recorded.Should().ContainSingle("the cap rejection must not log a second broadcast event");
    }

    [Fact]
    public async Task Cap_rejection_does_not_provision_a_conversation()
    {
        // The conversation is provisioned only AFTER a successful insert. A BR-9
        // cap rejection throws before any side-effect, so no orphan conversation
        // is created.
        var store = Build(out _, out _, out _, out var conversations,
            conversationId: "conv-should-not-happen");

        await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);
        conversations.Calls.Should().ContainSingle();

        var act = async () => await store.TryCreateWithLimitAsync(ValidInput(), limit: 1, CancellationToken.None);
        await act.Should().ThrowAsync<TooManyActiveRequestsException>();

        // Still exactly one provision — the rejected create provisioned nothing.
        conversations.Calls.Should().ContainSingle("the cap rejection must not provision a conversation");
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

    private sealed class RecordingConversationProvisioner : IConversationProvisioner
    {
        private readonly string? _conversationId;
        public List<(string RequestId, string ClientId)> Calls { get; } = new();

        public RecordingConversationProvisioner(string? conversationId) => _conversationId = conversationId;

        public Task<string?> CreateBroadcastingConversationAsync(string requestId, string clientId, CancellationToken ct)
        {
            Calls.Add((requestId, clientId));
            return Task.FromResult(_conversationId);
        }

        // S07: the create-path durable store does not advance to accepted (that is
        // OffersController orchestration), so this fake's advance is an unused no-op.
        public Task<string?> AdvanceToAcceptedAsync(
            string? conversationId, string winningJeeberId,
            IReadOnlyList<string> losingMemberIds, CancellationToken ct)
            => Task.FromResult<string?>(null);
    }

    private sealed class RecordingBroadcastEventRecorder : IBroadcastEventRecorder
    {
        private readonly BroadcastEventRecordOutcome _outcome;
        public List<(string ContextId, string Phase)> Recorded { get; } = new();

        public RecordingBroadcastEventRecorder(BroadcastEventRecordOutcome outcome) => _outcome = outcome;

        public Task<BroadcastEventRecordOutcome> RecordBroadcastingAsync(string contextId, string phase, CancellationToken ct)
        {
            Recorded.Add((contextId, phase));
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
