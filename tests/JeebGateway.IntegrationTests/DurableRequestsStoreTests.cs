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

    // -- requests-durable: durable read / list / mirror -----------------------

    private static DurableRequestsStore BuildWithMirror(
        out InMemoryRequestsStore inner,
        out RecordingDeliveryClient delivery,
        out RecordingMirror mirror)
    {
        inner = new InMemoryRequestsStore(Clock);
        delivery = new RecordingDeliveryClient();
        mirror = new RecordingMirror();
        return new DurableRequestsStore(
            inner, delivery,
            new RecordingBundleRecorder(SagaBundleRecordOutcome.Recorded),
            new RecordingConversationProvisioner(null),
            new RecordingBroadcastEventRecorder(BroadcastEventRecordOutcome.Recorded),
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance,
            mirror);
    }

    [Fact]
    public async Task Get_reads_through_to_delivery_service_when_inner_misses()
    {
        // Cold path (post-bounce): the in-memory mirror lost the row, so GetAsync
        // resolves it from delivery-service and maps the upstream projection.
        var store = BuildWithMirror(out _, out var delivery, out _);
        delivery.UpstreamRow = new DeliveryRequestUpstream
        {
            Id = "d-cold-1",
            ClientId = "client-9",
            Status = RequestStatus.HeadingOff,
            Description = "canonical row",
            TierId = "flash",
        };

        var row = await store.GetAsync("d-cold-1", CancellationToken.None);

        row.Should().NotBeNull();
        row!.Id.Should().Be("d-cold-1");
        row.ClientId.Should().Be("client-9");
        row.Status.Should().Be(RequestStatus.HeadingOff);
        row.Description.Should().Be("canonical row");
        delivery.GetDeliveryCalls.Should().Be(1);
    }

    [Fact]
    public async Task Get_returns_null_when_inner_misses_and_delivery_unavailable()
    {
        // A 404 / transport blip on the read-through degrades to null — exactly
        // today's unknown-id behaviour (controllers map null → 404). Never a 5xx.
        var store = BuildWithMirror(out _, out var delivery, out _);
        // UpstreamRow left null → GetDeliveryAsync throws (simulated fault).

        var row = await store.GetAsync("does-not-exist", CancellationToken.None);

        row.Should().BeNull();
        delivery.GetDeliveryCalls.Should().Be(1);
    }

    [Fact]
    public async Task Get_prefers_inner_and_never_reads_through_when_warm()
    {
        // Warm read: the row is in the in-memory mirror, so GetAsync returns it
        // verbatim and NEVER touches delivery-service (strict superset — identical
        // to today, full gateway field set intact).
        var store = BuildWithMirror(out _, out var delivery, out _);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        var row = await store.GetAsync(created.Id, CancellationToken.None);

        row.Should().NotBeNull();
        row!.Id.Should().Be(created.Id);
        delivery.GetDeliveryCalls.Should().Be(0, "a warm read must not round-trip to delivery-service");
    }

    [Fact]
    public async Task Create_mirrors_the_row_into_the_owner_list_mirror()
    {
        // The durable owner-list is backed by the gateway Postgres mirror; the
        // create path must upsert each new request into it so the list has rows.
        var store = BuildWithMirror(out _, out _, out var mirror);

        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        mirror.Upserted.Should().ContainSingle();
        mirror.Upserted[0].Id.Should().Be(created.Id);
        mirror.Upserted[0].ClientId.Should().Be("client-1");
    }

    [Fact]
    public async Task List_merges_durable_mirror_rows_with_inner_preferring_inner()
    {
        // The owner-list merges the in-memory rows OVER the durable mirror: the
        // in-memory row wins on an id collision (live status), while the mirror
        // contributes rows the in-memory model lost on a bounce. Oldest-first.
        var store = BuildWithMirror(out _, out _, out var mirror);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        // Stale durable snapshot of the SAME row (cancelled) + an older cold row
        // the in-memory model no longer holds.
        mirror.Rows.Add(new DeliveryRequest
        {
            Id = created.Id,
            ClientId = "client-1",
            Status = RequestStatus.Cancelled,
            Description = "stale mirror snapshot",
            CreatedAt = created.CreatedAt,
        });
        mirror.Rows.Add(new DeliveryRequest
        {
            Id = "cold-row",
            ClientId = "client-1",
            Status = RequestStatus.Delivered,
            Description = "survived a bounce",
            CreatedAt = created.CreatedAt.AddHours(-1),
        });

        var list = await store.ListForClientAsync("client-1", CancellationToken.None);

        list.Should().HaveCount(2);
        // Inner won the id collision — the live 'pending' status, not the mirror's stale 'cancelled'.
        list.Single(r => r.Id == created.Id).Status.Should().Be(RequestStatus.Pending);
        // The cold row (mirror-only) is surfaced.
        list.Should().Contain(r => r.Id == "cold-row" && r.Status == RequestStatus.Delivered);
        // Oldest-first: the -1h cold row precedes the just-created row.
        list[0].Id.Should().Be("cold-row");
    }

    [Fact]
    public async Task Cancel_mirrors_committed_status_into_the_mirror()
    {
        // A committed cancel reflects the resulting status onto the durable mirror
        // so a post-bounce owner-list shows the cancel.
        var store = BuildWithMirror(out _, out _, out var mirror);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        var allowedFrom = new HashSet<string>(StringComparer.Ordinal) { RequestStatus.Pending };
        var result = await store.TryCancelAsync(
            created.Id, allowedFrom, RequestStatus.Cancelled,
            cancelledBy: "client", reason: "changed my mind", at: Clock.GetUtcNow(), CancellationToken.None);

        result.Should().NotBeNull();
        result!.Outcome.Should().Be(CancellationStoreOutcome.Committed);
        mirror.Cancels.Should().ContainSingle();
        mirror.Cancels[0].Id.Should().Be(created.Id);
        mirror.Cancels[0].GwStatus.Should().Be(RequestStatus.Cancelled);
    }

    [Fact]
    public async Task SetStatus_mirrors_the_new_status_into_the_owner_list_mirror()
    {
        // F4: an owner-list-visible status mutation must be reflected onto the durable
        // mirror so a post-bounce list shows the live status, not the create-time
        // 'pending'. Previously SetStatusAsync delegated to the in-memory store only.
        var store = BuildWithMirror(out _, out _, out var mirror);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        var ok = await store.SetStatusAsync(created.Id, RequestStatus.HeadingOff, CancellationToken.None);

        ok.Should().BeTrue();
        mirror.Updates.Should().ContainSingle();
        mirror.Updates[0].Id.Should().Be(created.Id);
        mirror.Updates[0].GwStatus.Should().Be(RequestStatus.HeadingOff);
        mirror.Updates[0].GwJeeberId.Should().BeNull("a status-only mutation leaves the jeeber column untouched");
    }

    [Fact]
    public async Task SetJeeberId_mirrors_the_assignment_into_the_owner_list_mirror()
    {
        // F4: assigning the jeeber must reflect onto the durable owner-list.
        var store = BuildWithMirror(out _, out _, out var mirror);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        var ok = await store.SetJeeberIdAsync(created.Id, "jeeber-77", CancellationToken.None);

        ok.Should().BeTrue();
        mirror.Updates.Should().ContainSingle();
        mirror.Updates[0].GwJeeberId.Should().Be("jeeber-77");
        mirror.Updates[0].GwStatus.Should().BeNull();
    }

    [Fact]
    public async Task SetAcceptedFee_mirrors_the_fee_into_the_owner_list_mirror()
    {
        // F4: the accepted fee must reflect onto the durable owner-list.
        var store = BuildWithMirror(out _, out _, out var mirror);
        var created = await store.TryCreateWithLimitAsync(ValidInput(), limit: 3, CancellationToken.None);

        var ok = await store.TrySetAcceptedFeeAsync(created.Id, 42.5m, CancellationToken.None);

        ok.Should().BeTrue();
        mirror.Updates.Should().ContainSingle();
        mirror.Updates[0].GwAcceptedFee.Should().Be(42.5m);
    }

    [Fact]
    public async Task SetStatus_on_unknown_id_does_not_mirror()
    {
        // A failed (no-op) mutation must NOT touch the mirror — nothing changed.
        var store = BuildWithMirror(out _, out _, out var mirror);

        var ok = await store.SetStatusAsync("never-created", RequestStatus.HeadingOff, CancellationToken.None);

        ok.Should().BeFalse();
        mirror.Updates.Should().BeEmpty("a mutation that changed nothing must not write to the mirror");
    }

    // -- fakes ---------------------------------------------------------------

    private sealed class RecordingDeliveryClient : NotImplementedDeliveryClient
    {
        public List<CreateDeliveryRowUpstream> CreatedRows { get; } = new();
        public string? EchoMismatchedId { get; init; }

        /// <summary>requests-durable: canned read-through row. When set (and the id
        /// matches) <see cref="GetDeliveryAsync"/> returns it; otherwise it throws
        /// to simulate a 404 / transport fault so the decorator degrades to null.</summary>
        public DeliveryRequestUpstream? UpstreamRow { get; set; }

        /// <summary>Counts read-through calls so a warm read can prove it never
        /// touched delivery-service.</summary>
        public int GetDeliveryCalls { get; private set; }

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

        public override Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
        {
            GetDeliveryCalls++;
            if (UpstreamRow is not null && string.Equals(UpstreamRow.Id, deliveryId, StringComparison.Ordinal))
            {
                return Task.FromResult(UpstreamRow);
            }
            throw new InvalidOperationException("simulated delivery-service 404 / transport fault");
        }
    }

    /// <summary>
    /// requests-durable: in-memory <see cref="IDurableRequestsMirror"/> double. Tracks
    /// create-upserts + cancel-mirrors and returns whatever <see cref="Rows"/> is
    /// seeded with for the owner-list (so the merge-preferring-inner can be exercised).
    /// </summary>
    private sealed class RecordingMirror : IDurableRequestsMirror
    {
        public List<DeliveryRequest> Upserted { get; } = new();
        public List<(string Id, string GwStatus)> Cancels { get; } = new();
        public List<DeliveryRequest> Rows { get; } = new();
        public List<(string Id, string? GwStatus, string? GwJeeberId, decimal? GwAcceptedFee)> Updates { get; } = new();

        public Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct)
        {
            Upserted.Add(row);
            return Task.CompletedTask;
        }

        public Task UpdateLifecycleAsync(
            string requestId, string? gwStatus, string? gwJeeberId, decimal? gwAcceptedFee,
            DateTimeOffset at, CancellationToken ct)
        {
            Updates.Add((requestId, gwStatus, gwJeeberId, gwAcceptedFee));
            return Task.CompletedTask;
        }

        public Task MarkCancelledAsync(
            string requestId, string gwStatus, string? cancelledBy,
            string? cancellationReason, DateTimeOffset at, CancellationToken ct)
        {
            Cancels.Add((requestId, gwStatus));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
        {
            IReadOnlyList<DeliveryRequest> rows = Rows
                .Where(r => string.Equals(r.ClientId, clientId, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(rows);
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

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotImplementedException();

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotImplementedException();
        public virtual Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotImplementedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotImplementedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotImplementedException();
    }
}
