using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P1 (device-QA regression) — at offer-accept the chat conversation was not ready:
/// <c>conversationId</c> stayed null and the winning jeeber could not be seated, so the
/// jeeber read 403 on the chat thread and seating failed.
///
/// <para>FIX (gateway-only orchestration over the EXISTING chat-service conversation
/// endpoint — the gateway is the SOLE chat caller, org no-coupling law): on a committed
/// accept the V1 controller now ENSURES the conversation exists (resolve by correlation
/// key == requestId, else create it with the snake_case <c>correlation_key</c>/<c>owner_user_id</c>
/// body — chat-service is idempotent on the correlation key, INV-3), LINKS the returned
/// <c>conversationId</c> onto the local request projection, THEN seats the winning jeeber as a
/// <c>jeeber_winner</c> participant.</para>
///
/// <para>chat-service is replaced by a recording <see cref="IJeebConversationClient"/> fake so
/// the create→link→seat ordering and the snake_case-shaped payload are asserted
/// deterministically. DEGRADE-DON'T-FAIL: the accept saga already committed, so any chat blip /
/// disabled flag is logged and swallowed — the accept stays 200.</para>
///
/// <para><b>GW5 / W1.6-gateway.</b> The seat and the phase advance were TWO chat-service
/// requests; they are now ONE <c>POST /api/conversations/{id}/settle</c>. Every assertion
/// below therefore comes in a PAIR: the settle carries the right end state, AND the two
/// older calls were not made. The second half is what makes the first mean something — a
/// test that only asserted "settle happened" would still pass if the old sequence were
/// left running alongside it, which is the exact half-state GW5 exists to remove.</para>
/// </summary>
// GW5: joins the chat-settle collection so it never runs concurrently with G4's counter
// deltas — this class drives ChatSettleTelemetry.Failures through the accept controller's
// degrade-don't-fail catch, and those counters are static and untagged.
[Collection(JeebGateway.IntegrationTests.Gw5Pack.Gw5ChatSettleCollection.Name)]
public class S03AcceptConversationSeatTests
{
    private const string ClientOwner = "client-owner";
    // c2-1: the accept guard resolves the winner to a wallet holder, so this id is a GUID.
    private const string Winner = "0a5f37e6-8c14-4b92-a7d3-51e9026bf48c";

    [Fact]
    public async Task Accept_WhenNoConversation_CreatesIt_LinksId_AndSeatsWinner()
    {
        var convo = new RecordingConversationClient(); // ExistingConversationId null → create path
        using var factory = NewFactory(convo, chat: true);

        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-c1", requestId, Winner);

        var resp = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-c1/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // A conversation was CREATED with the snake_case-shaped body: correlation_key == requestId
        // (idempotency authority) and owner_user_id == the request-owning client.
        convo.CreateCalls.Should().ContainSingle();
        convo.CreateCalls.Single().RequestId.Should().Be(requestId);     // -> correlation_key
        convo.CreateCalls.Single().ClientUserId.Should().Be(ClientOwner); // -> owner_user_id

        // GW5 — ONE call carries the whole end state: the winner is seated as
        // jeeber_winner (so chat opens, no 403), the conversation is advanced OUT of the
        // auction phase into the settled 1:1, and the losing bidders are removed.
        convo.Settles.Should().ContainSingle();
        var settle = convo.Settles.Single();
        settle.ConversationId.Should().Be(RecordingConversationClient.CreatedId);
        settle.Phase.Should().Be("accepted");
        settle.WinnerUserId.Should().Be(Winner);
        settle.WinnerRoleInConvo.Should().Be("jeeber_winner");
        settle.RemoveOthers.Should().BeTrue();

        // THE OTHER HALF, and the one that actually proves the window is gone: the
        // pre-GW5 add-participant → advance-phase pair is NOT issued. Between those two
        // requests the winner sat in a pre-settlement conversation with every losing
        // bidder still active, and the accept had already committed so nothing could be
        // rolled back. If either of these ever goes non-empty again, that window is back.
        convo.Seats.Should().BeEmpty("GW5 folds the seat into the settle — two writes is the defect");
        convo.PhaseAdvances.Should().BeEmpty("GW5 folds the phase advance into the settle");

        // The resolved conversationId is LINKED onto the projection the client reads.
        var body = await resp.Content.ReadFromJsonAsync<AcceptBody>();
        body!.ConversationId.Should().Be(RecordingConversationClient.CreatedId);
    }

    [Fact]
    public async Task Accept_WhenConversationAlreadyExists_DoesNotCreate_ButStillSeatsWinner()
    {
        // The client created the conversation at order time; the by-correlation lookup resolves it.
        var convo = new RecordingConversationClient { ExistingConversationId = "conv-existing" };
        using var factory = NewFactory(convo, chat: true);

        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-c2", requestId, Winner);

        var resp = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-c2/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        convo.CreateCalls.Should().BeEmpty("an existing conversation must be reused, not re-created");
        convo.Settles.Should().ContainSingle();
        convo.Settles.Single().ConversationId.Should().Be("conv-existing");
        convo.Settles.Single().WinnerUserId.Should().Be(Winner);
        convo.Seats.Should().BeEmpty();
        convo.PhaseAdvances.Should().BeEmpty();
    }

    [Fact]
    public async Task Accept_WhenChatFlagOff_DoesNotTouchChat_AndStaysHttp200()
    {
        // Negative / gate: with the Chat upstream flag off the accept must not call chat at all.
        var convo = new RecordingConversationClient();
        using var factory = NewFactory(convo, chat: false);

        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-c3", requestId, Winner);

        var resp = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-c3/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        convo.CreateCalls.Should().BeEmpty();
        convo.Seats.Should().BeEmpty();
        convo.Settles.Should().BeEmpty();
    }

    [Fact]
    public async Task Accept_WhenChatServiceFaultsOnSettle_DegradesToHttp200()
    {
        // Degrade-don't-fail: the saga already committed upstream, so a chat-service blip on
        // the settle call must NOT turn a committed accept into a 5xx. GW5 changes what
        // happens NEXT, not this: the request row already carries the assignment, so
        // AcceptChatSettleReconciler can find and heal it (asserted in
        // Gw5AcceptSettleReconcileTests — this test only pins the 200).
        var convo = new RecordingConversationClient { ThrowOnSettle = true };
        using var factory = NewFactory(convo, chat: true);

        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-c4", requestId, Winner);

        var resp = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-c4/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        convo.SettleAttempts.Should().BeGreaterThanOrEqualTo(1);
        convo.Settles.Should().BeEmpty("the fault means nothing landed on chat-service");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(IJeebConversationClient convo, bool chat)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        { "FeatureFlags:UseUpstream:Chat", chat ? "true" : "false" },
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(new FakeAcceptOfferClient(Winner));
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton<IDeliveryServiceClient>(new NoopDeliveryClient());
                    services.RemoveAll<IJeebConversationClient>();
                    services.AddSingleton(convo);
                });
            });

    private static async Task<string> SeedRequestAsync(WebApplicationFactory<Program> factory, string clientId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = 33.5138, Lng = 36.2765 },
            DropoffLocation = new GeoPoint { Lat = 33.52, Lng = 36.28 },
        }, CancellationToken.None);
        return created.Id;
    }

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    private sealed record AcceptBody(string Id, string ClientId, string Status, string? JeeberId, string? ConversationId);

    /// <summary>
    /// Recording test double for chat-service's conversation aggregate. Exercises only the three
    /// seams the post-accept orchestration uses (resolve-by-correlation / create / seat). Every
    /// other member throws — the accept path must not call them.
    /// </summary>
    private sealed class RecordingConversationClient : IJeebConversationClient
    {
        public const string CreatedId = "conv-created";

        /// <summary>When set, the by-correlation lookup resolves this id (no create). When null,
        /// the lookup signals 404 (NotFound) so the create path fires.</summary>
        public string? ExistingConversationId { get; init; }
        public bool ThrowOnSettle { get; init; }

        public ConcurrentQueue<CreateJeebConversationRequest> CreateCalls { get; } = new();
        public ConcurrentQueue<SeatRecord> Seats { get; } = new();
        public ConcurrentQueue<AdvanceRecord> PhaseAdvances { get; } = new();
        public ConcurrentQueue<SettleRecord> Settles { get; } = new();
        public int SeatAttempts { get; private set; }
        public int SettleAttempts { get; private set; }

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(string correlationKey, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(ExistingConversationId))
                throw new JeebConversationApiException(HttpStatusCode.NotFound, null);
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = ExistingConversationId!,
                CorrelationKey = correlationKey,
                Phase = "broadcasting",
            });
        }

        public Task<JeebConversationResponse> CreateConversationAsync(CreateJeebConversationRequest request, CancellationToken ct)
        {
            CreateCalls.Enqueue(request);
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = CreatedId,
                CorrelationKey = request.RequestId,
                Phase = request.Phase,
            });
        }

        // KEPT RECORDING, deliberately. The accept path must no longer call this; the
        // tests assert Seats is EMPTY. A stub that threw here would fail the same tests
        // for a different reason and hide whether the call was made at all.
        public Task<JeebConversationParticipant> AddParticipantAsync(string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
        {
            SeatAttempts++;
            Seats.Enqueue(new SeatRecord(conversationId, request.UserId, request.RoleInConvo));
            return Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
            });
        }

        public Task<JeebMessageResponse> AppendMessageAsync(string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(string conversationId, string viewerUserId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebConversationMembership> GetMembershipAsync(string conversationId, string viewerUserId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeebConversationResponse> AdvancePhaseAsync(string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
        {
            PhaseAdvances.Enqueue(new AdvanceRecord(
                conversationId, request.Phase, request.WinnerUserId, request.WinnerRoleInConvo, request.RemoveOthers));
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                Phase = request.Phase,
            });
        }

        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct)
        {
            SettleAttempts++;
            if (ThrowOnSettle)
                throw new JeebConversationApiException(HttpStatusCode.ServiceUnavailable, "chat-service unavailable");
            Settles.Enqueue(new SettleRecord(
                conversationId, request.Phase, request.WinnerUserId,
                request.WinnerRoleInConvo, request.RemoveOthers));
            return Task.FromResult(new JeebConversationSettleResponse
            {
                // The envelope shape chat-service actually returns: the conversation is
                // NESTED, not flattened. A fake that returned a bare ConversationResponse
                // here would hide the very binding trap the client guards against.
                Conversation = new JeebConversationResponse
                {
                    ConversationId = conversationId,
                    Phase = request.Phase,
                    Participants = new List<JeebConversationParticipant>
                    {
                        new() { UserId = request.WinnerUserId, RoleInConvo = request.WinnerRoleInConvo },
                    },
                },
                Seated = true,
                PhaseChanged = true,
            });
        }
    }

    private sealed record SeatRecord(string ConversationId, string UserId, string Role);

    private sealed record SettleRecord(
        string ConversationId, string Phase, string WinnerUserId, string WinnerRoleInConvo, bool RemoveOthers);

    private sealed record AdvanceRecord(
        string ConversationId, string Phase, string? WinnerUserId, string WinnerRoleInConvo, bool RemoveOthers);

    /// <summary>Offer-service double: only the accept-with-status seam is used; returns an accepted
    /// envelope carrying the winning jeeber. Every other member throws.</summary>
    private sealed class FakeAcceptOfferClient : IOfferServiceClient
    {
        private readonly OfferAcceptResult _result;
        public FakeAcceptOfferClient(string winningJeeberId)
            => _result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer",
                    JeeberId = winningJeeberId,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            };

        public Task<OfferAcceptResult> AcceptWithStatusAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(_result);
        public Task<OfferAcceptWire> AcceptAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>Delivery-service double: the post-accept delivery-leg sync writes the row; record
    /// nothing, never fault (the delivery leg is asserted in JeebOffersAcceptDeliveryLegTests).
    /// Every other member throws.</summary>
    private sealed class NoopDeliveryClient : IDeliveryServiceClient
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

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult(0);
    }
}
