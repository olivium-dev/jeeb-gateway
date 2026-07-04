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
/// </summary>
public class S03AcceptConversationSeatTests
{
    private const string ClientOwner = "client-owner";
    private const string Winner = "jeeber-win";

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

        // The winning jeeber was SEATED on that conversation as jeeber_winner (so chat opens, no 403).
        convo.Seats.Should().ContainSingle();
        convo.Seats.Single().ConversationId.Should().Be(RecordingConversationClient.CreatedId);
        convo.Seats.Single().UserId.Should().Be(Winner);
        convo.Seats.Single().Role.Should().Be("jeeber_winner");

        // The conversation was ADVANCED to the settled 1:1 (accepted) phase, promoting the
        // winner and removing losing bidders — so chat-service can safely let the winner
        // read the client's messages without any loser left seated. (winner-blind-to-client fix)
        convo.PhaseAdvances.Should().ContainSingle();
        var advance = convo.PhaseAdvances.Single();
        advance.ConversationId.Should().Be(RecordingConversationClient.CreatedId);
        advance.Phase.Should().Be("accepted");
        advance.WinnerUserId.Should().Be(Winner);
        advance.WinnerRoleInConvo.Should().Be("jeeber_winner");
        advance.RemoveOthers.Should().BeTrue();

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
        convo.Seats.Should().ContainSingle();
        convo.Seats.Single().ConversationId.Should().Be("conv-existing");
        convo.Seats.Single().UserId.Should().Be(Winner);
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
    }

    [Fact]
    public async Task Accept_WhenChatServiceFaultsOnSeat_DegradesToHttp200()
    {
        // Degrade-don't-fail: the saga already committed upstream, so a chat-service blip on the
        // seat call must NOT turn a committed accept into a 5xx (the jeeber reads 403 until reconciled).
        var convo = new RecordingConversationClient { ThrowOnSeat = true };
        using var factory = NewFactory(convo, chat: true);

        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-c4", requestId, Winner);

        var resp = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-c4/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        convo.SeatAttempts.Should().BeGreaterThanOrEqualTo(1);
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
        public bool ThrowOnSeat { get; init; }

        public ConcurrentQueue<CreateJeebConversationRequest> CreateCalls { get; } = new();
        public ConcurrentQueue<SeatRecord> Seats { get; } = new();
        public ConcurrentQueue<AdvanceRecord> PhaseAdvances { get; } = new();
        public int SeatAttempts { get; private set; }

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

        public Task<JeebConversationParticipant> AddParticipantAsync(string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
        {
            SeatAttempts++;
            if (ThrowOnSeat)
                throw new JeebConversationApiException(HttpStatusCode.ServiceUnavailable, "chat-service unavailable");
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
    }

    private sealed record SeatRecord(string ConversationId, string UserId, string Role);

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
