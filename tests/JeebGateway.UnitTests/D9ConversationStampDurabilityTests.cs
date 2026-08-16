using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Requests;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Durable;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>
/// D9 (Phase V run 2, 2026-08-16): chat push was SKIPPED in both directions on a live
/// accepted order, because conversation → delivery-request resolution returned nothing.
///
/// <para>Root cause: <c>DurableRequestsStore</c> "stamped" the conversation id by assigning
/// <c>row.ConversationId</c> on the row its inner store had just returned, plus a write to
/// <c>IDurableRequestsMirror</c>. Both legs are dead in production: the mirror has had NO DI
/// registration since gwdbx W5-11, and the inner store is now <c>UpstreamRequestsStore</c>, a
/// stateless HTTP adapter that maps a FRESH <c>DeliveryRequest</c> on every read — so the
/// assignment mutated a throwaway object. The READ side does delegate upstream, so it could
/// never see a stamp nobody wrote.</para>
///
/// <para>Live confirmation, gateway journal, order ORD-C680AF:
/// <c>Post-accept chat settle for request aa8a3b3b-…-20220dc680af (conversation
/// 318fe628-…)</c> at 15:00:08, then at 15:00:59 <c>Chat push SKIPPED: conversation
/// 318fe628-… resolves to no delivery request row</c>. delivery-service's
/// <c>/api/v1/requests/by-conversation/{id}</c> route exists (401 with no service credential,
/// versus 404 for an unmounted path), so the read rail was fine — nothing had been stamped.</para>
///
/// <para>These tests run the store over an inner store with production's STATELESS shape.
/// The pre-existing suite could not catch this: it uses an in-memory inner (so the caller
/// mutates the very row the store keeps) plus a mirror, and it is
/// <c>&lt;Compile Remove&gt;</c>-d out of the integration project entirely.</para>
/// </summary>
public sealed class D9ConversationStampDurabilityTests
{
    private const string Client = "client-1";
    private const string Jeeber = "jeeber-1";

    [Fact]
    public async Task The_accept_time_stamp_survives_a_stateless_inner_store()
    {
        var store = Build(out var inner);
        var created = await store.TryCreateWithLimitAsync(Input(), limit: 3, CancellationToken.None);
        await inner.SetJeeberIdAsync(created.Id, Jeeber, CancellationToken.None);

        // Precondition: nothing resolves this conversation yet, so a pass cannot be pre-baked.
        (await store.GetByConversationIdAsync("conv-d9", CancellationToken.None)).Should().BeNull();

        await store.SetConversationIdAsync(created.Id, "conv-d9", CancellationToken.None);

        var resolved = await store.GetByConversationIdAsync("conv-d9", CancellationToken.None);
        resolved.Should().NotBeNull(
            "ChatMessagePushNotifier resolves its recipients through exactly this lookup, and "
            + "a null here is a total, silent loss of chat push for the order");
        resolved!.Id.Should().Be(created.Id);
        // Both delivery principals: they ARE the chat push recipient set.
        resolved.ClientId.Should().Be(Client);
        resolved.JeeberId.Should().Be(Jeeber);
    }

    [Fact]
    public async Task An_unstamped_conversation_still_resolves_to_nothing()
    {
        // Control for the test above, and one shown capable of a DIFFERENT answer in the
        // same run: the same lookup on the same store returns a row for the stamped id.
        var store = Build(out _);
        var created = await store.TryCreateWithLimitAsync(Input(), limit: 3, CancellationToken.None);
        await store.SetConversationIdAsync(created.Id, "conv-d9-stamped", CancellationToken.None);

        (await store.GetByConversationIdAsync("conv-d9-stamped", CancellationToken.None))
            .Should().NotBeNull();
        (await store.GetByConversationIdAsync("conv-d9-absent", CancellationToken.None))
            .Should().BeNull("the fix must persist a real stamp, not answer every lookup");
    }

    [Fact]
    public async Task The_create_time_stamp_survives_a_stateless_inner_store()
    {
        // Same defect on the create leg: PersistSagaAsync assigned created.ConversationId
        // on the row the inner store had already handed back. A pre-accept chat message
        // resolves its recipients through the same lookup.
        var store = Build(out _, provisionedConversationId: "conv-d9-create");

        var created = await store.TryCreateWithLimitAsync(Input(), limit: 3, CancellationToken.None);

        created.ConversationId.Should().Be("conv-d9-create");
        var resolved = await store.GetByConversationIdAsync("conv-d9-create", CancellationToken.None);
        resolved.Should().NotBeNull();
        resolved!.Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task A_stamp_the_inner_store_refuses_degrades_instead_of_failing_the_saga()
    {
        // IRequestsStore.SetConversationIdAsync is best-effort by contract and the accept
        // saga has already committed, so a refusing owner must not surface here.
        var refusing = Substitute.For<IRequestsStore>();
        refusing.SetConversationIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new HttpRequestException("owner refused the stamp")));
        var store = BuildOver(refusing, provisionedConversationId: null);

        var act = async () => await store.SetConversationIdAsync(
            "req-1", "conv-d9-refused", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ---------------------------------------------------------------------
    // fixture
    // ---------------------------------------------------------------------

    private static DurableRequestsStore Build(
        out StatelessInnerStore inner, string? provisionedConversationId = null)
    {
        inner = new StatelessInnerStore(TimeProvider.System);
        return BuildOver(inner, provisionedConversationId);
    }

    private static DurableRequestsStore BuildOver(
        IRequestsStore inner, string? provisionedConversationId)
    {
        var delivery = Substitute.For<IDeliveryServiceClient>();
        delivery.CreateDeliveryRowAsync(Arg.Any<CreateDeliveryRowUpstream>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new DeliveryRowUpstream
            {
                DeliveryId = call.Arg<CreateDeliveryRowUpstream>().Id,
            }));

        var bundles = Substitute.For<ISagaBundleRecorder>();
        bundles.RecordCreatedAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(SagaBundleRecordOutcome.Recorded));

        var conversations = Substitute.For<IConversationProvisioner>();
        conversations.CreateBroadcastingConversationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(provisionedConversationId));

        var broadcasts = Substitute.For<IBroadcastEventRecorder>();
        broadcasts.RecordBroadcastingAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(BroadcastEventRecordOutcome.Recorded));

        return new DurableRequestsStore(
            inner, delivery, bundles, conversations, broadcasts,
            Options.Create(new DurableRequestsOptions { Enabled = true }),
            NullLogger<DurableRequestsStore>.Instance,
            // Null, exactly as in production since W5-11 — no mirror leg to hide behind.
            mirror: null);
    }

    private static CreateRequestInput Input() => new()
    {
        ClientId = Client,
        Description = "deliver a package",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 25.2, Lng = 55.3 },
        DropoffLocation = new GeoPoint { Lat = 25.4, Lng = 55.5 },
    };

    /// <summary>
    /// The shape UpstreamRequestsStore has live: every read hands back a DETACHED row, so
    /// mutating what a read returned persists nothing. SetConversationIdAsync is the only
    /// way the conversation id reaches anything durable.
    /// </summary>
    internal sealed class StatelessInnerStore : IRequestsStore
    {
        private readonly InMemoryRequestsStore _model;

        public StatelessInnerStore(TimeProvider clock) => _model = new InMemoryRequestsStore(clock);

        public Task SetConversationIdAsync(string requestId, string conversationId, CancellationToken ct)
            => _model.SetConversationIdAsync(requestId, conversationId, ct);

        public async Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct)
            => Detach(await _model.CreateAsync(input, ct))!;

        public async Task<DeliveryRequest> TryCreateWithLimitAsync(
            CreateRequestInput input, int limit, CancellationToken ct)
            => Detach(await _model.TryCreateWithLimitAsync(input, limit, ct))!;

        public async Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
            => Detach(await _model.GetAsync(requestId, ct));

        public async Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct)
            => Detach(await _model.GetByConversationIdAsync(conversationId, ct));

        public async Task<DeliveryRequest?> TryAcceptByJeeberAsync(
            string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct)
            => Detach(await _model.TryAcceptByJeeberAsync(requestId, jeeberId, limit, at, ct));

        public async Task<DeliveryRequest?> MarkClientUnreachableAsync(
            string requestId, DateTimeOffset at, CancellationToken ct)
            => Detach(await _model.MarkClientUnreachableAsync(requestId, at, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
            => DetachAll(await _model.ListForClientAsync(clientId, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
            => DetachAll(await _model.ListForJeeberAsync(jeeberId, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(
            DateTimeOffset cutoff, CancellationToken ct)
            => DetachAll(await _model.ListPendingCreatedAtOrBeforeAsync(cutoff, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(
            DateTimeOffset cutoff, CancellationToken ct)
            => DetachAll(await _model.ListScheduledDueAsync(cutoff, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
            => DetachAll(await _model.ListAssignedSinceAsync(since, limit, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(
            string jeeberId, CancellationToken ct)
            => DetachAll(await _model.ListJeeberCancelledAsync(jeeberId, ct));

        public async Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(
            DateTimeOffset cutoff, CancellationToken ct)
            => DetachAll(await _model.ListUnreachableAtOrBeforeAsync(cutoff, ct));

        public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct)
            => _model.CountActiveForClientAsync(clientId, ct);
        public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct)
            => _model.CountActiveForJeeberAsync(jeeberId, ct);
        public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct)
            => _model.SetStatusAsync(requestId, status, ct);
        public Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct)
            => _model.SetJeeberIdAsync(requestId, jeeberId, ct);
        public Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct)
            => _model.TrySetAcceptedFeeAsync(requestId, fee, ct);
        public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct)
            => _model.TryExpireAsync(requestId, at, ct);
        public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct)
            => _model.TryActivateScheduledAsync(requestId, at, ct);
        public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct)
            => _model.AnonymizeForClientAsync(userId, anonymizedHash, ct);
        public Task<CancellationStoreResult?> TryCancelAsync(
            string requestId, IReadOnlySet<string> allowedFromStates, string targetStatus,
            string cancelledBy, string? reason, DateTimeOffset at, CancellationToken ct)
            => _model.TryCancelAsync(requestId, allowedFromStates, targetStatus, cancelledBy, reason, at, ct);
        public Task<CancellationStoreResult?> TryDecideCancellationAsync(
            string requestId, bool approve, DateTimeOffset at, CancellationToken ct)
            => _model.TryDecideCancellationAsync(requestId, approve, at, ct);
        public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(
            int page, int pageSize, CancellationToken ct)
            => _model.ListPendingCancellationsAsync(page, pageSize, ct);
        public Task<OtpVerificationResult> TryVerifyOtpAsync(
            string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct)
            => _model.TryVerifyOtpAsync(requestId, otpCode, maxAttempts, at, ct);
        public Task<bool> TrySetEscalationIdAsync(string requestId, string escalationId, CancellationToken ct)
            => _model.TrySetEscalationIdAsync(requestId, escalationId, ct);

        private static IReadOnlyList<DeliveryRequest> DetachAll(IReadOnlyList<DeliveryRequest> rows)
            => rows.Select(row => Detach(row)!).ToList();

        private static DeliveryRequest? Detach(DeliveryRequest? row) => row is null ? null : new DeliveryRequest
        {
            Id = row.Id,
            ClientId = row.ClientId,
            Status = row.Status,
            Description = row.Description,
            Transcription = row.Transcription,
            TranscriptionConfidence = row.TranscriptionConfidence,
            AudioUrl = row.AudioUrl,
            Photos = row.Photos,
            TierId = row.TierId,
            PickupLocation = row.PickupLocation,
            DropoffLocation = row.DropoffLocation,
            PickupAddress = row.PickupAddress,
            DropoffAddress = row.DropoffAddress,
            RecipientPhone = row.RecipientPhone,
            CreatedAt = row.CreatedAt,
            ScheduledAt = row.ScheduledAt,
            ActivatedAt = row.ActivatedAt,
            ExpiredAt = row.ExpiredAt,
            JeeberId = row.JeeberId,
            AcceptedAt = row.AcceptedAt,
            AcceptedFee = row.AcceptedFee,
            ConversationId = row.ConversationId,
            DeliveryOtp = row.DeliveryOtp,
            OtpAttemptCount = row.OtpAttemptCount,
            OtpLockedAt = row.OtpLockedAt,
            ClientUnreachableAt = row.ClientUnreachableAt,
            OtpEscalationId = row.OtpEscalationId,
        };
    }
}
