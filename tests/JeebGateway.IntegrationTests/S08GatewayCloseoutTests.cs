using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 gateway closeout — the three GATEWAY-side gaps the S08 suite reds traced to:
/// <list type="bullet">
///   <item><b>B</b> (membership seeding) — POST /requests/{id}/offers seats the
///     offering jeeber as a <c>jeeber_offerer</c> participant on the request's
///     conversation aggregate (degrade-don't-fail; never flips the 201), so the
///     offer jeebers can read (200) and non-members 403.</item>
///   <item><b>D-accept</b> (H7/N9) — POST /offers/{id}/accept enriches the accept
///     response with <c>winner_user_id</c> + <c>conversation_phase</c> WITHOUT
///     leaking delivery status (status stays "accepted"), advancing the conversation
///     aggregate phase via chat-service's PATCH /api/conversations/{id}/phase.</item>
///   <item><b>D-WS</b> (H6) — GET /v1/realtime/jeeb:chat:{id} mints a short-lived
///     signed membership ticket for a member so realtime can authorize the WS join
///     without calling chat-service (no inter-service coupling).</item>
/// </list>
/// All paths are flag-gated on FeatureFlags:UseUpstream:Chat (and :Offer for the
/// accept path). The gateway computes NO membership / phase — it forwards to and
/// reads back from chat-service, the authority.
/// </summary>
public sealed class S08GatewayCloseoutTests
{
    // -----------------------------------------------------------------
    // B — offer-submit seats the offering jeeber on the conversation.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_SeatsOfferingJeeber_OnConversation_AsJeeberOfferer()
    {
        var chat = new RecordingJeebConversationClient();
        using var factory = NewFactory(chat, chatEnabled: true);

        var conversationId = "conv-b-" + Guid.NewGuid();
        var requestId = await SeedRequestAsync(factory, "client-b", conversationId);
        var jeeberId = "jeeber-b-" + Guid.NewGuid();

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 12.5m, etaMinutes = 30, note = "On my way" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        // The gateway seated the OFFERING jeeber as jeeber_offerer on the request's
        // conversation — forwarding only (conversationId, jeeberId, role).
        chat.AddParticipantCalls.Should().Be(1);
        chat.LastAddParticipantConversationId.Should().Be(conversationId);
        chat.LastAddParticipant!.UserId.Should().Be(jeeberId);
        chat.LastAddParticipant.RoleInConvo.Should().Be("jeeber_offerer");
    }

    [Fact]
    public async Task Submit_WhenChatSeatingThrows_StillReturns201_DegradeDontFail()
    {
        // chat-service blip on the seat call must NEVER turn the durable offer 201
        // into a 5xx — the seat is best-effort.
        var chat = new RecordingJeebConversationClient
        {
            AddParticipantThrows = new JeebConversationApiException(HttpStatusCode.BadGateway, "boom"),
        };
        using var factory = NewFactory(chat, chatEnabled: true);

        var requestId = await SeedRequestAsync(factory, "client-b2", "conv-b2-" + Guid.NewGuid());

        var resp = await JeeberClient(factory, "jeeber-b2-" + Guid.NewGuid()).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        chat.AddParticipantCalls.Should().Be(1);
    }

    [Fact]
    public async Task Submit_WhenNoConversationIdOnRequest_DoesNotSeat_StillReturns201()
    {
        var chat = new RecordingJeebConversationClient();
        using var factory = NewFactory(chat, chatEnabled: true);

        // No conversation id stamped on the request (chat was down at create).
        var requestId = await SeedRequestAsync(factory, "client-b3", conversationId: null);

        var resp = await JeeberClient(factory, "jeeber-b3-" + Guid.NewGuid()).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        chat.AddParticipantCalls.Should().Be(0);
    }

    [Fact]
    public async Task Submit_WhenChatFlagOff_DoesNotSeat_StillReturns201()
    {
        var chat = new RecordingJeebConversationClient();
        using var factory = NewFactory(chat, chatEnabled: false);

        var requestId = await SeedRequestAsync(factory, "client-b4", "conv-b4-" + Guid.NewGuid());

        var resp = await JeeberClient(factory, "jeeber-b4-" + Guid.NewGuid()).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = 9m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        chat.AddParticipantCalls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // D-accept (H7/N9) — accept DTO carries winner_user_id + conversation_phase.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_EnrichesDto_With_WinnerUserId_And_ConversationPhase_NoDeliveryLeak()
    {
        var chat = new RecordingJeebConversationClient { AdvancedPhase = "accepted" };
        using var factory = NewFactory(chat, chatEnabled: true, offerEnabled: true,
            fakeOffer: AcceptedFake("offer-d", "jeeber-kamal"));

        var requestId = await SeedRequestAsync(factory, "client-d", "conv-d-" + Guid.NewGuid());
        SeedRouting(factory, "offer-d", requestId, "jeeber-kamal");

        var resp = await ClientActor(factory, "client-d").PostAsync("/offers/offer-d/accept", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());

        // S07 fields preserved — status is the OFFER outcome, NOT a delivery state.
        json["id"]!.Value<string>().Should().Be(requestId);
        json["status"]!.Value<string>().Should().Be("accepted");
        json["jeeberId"]!.Value<string>().Should().Be("jeeber-kamal");

        // S08 additions (snake_case, the exact keys H7/N9 assert).
        json["winner_user_id"]!.Value<string>().Should().Be("jeeber-kamal");
        json["conversation_phase"]!.Value<string>().Should().Be("accepted");

        // The conversation aggregate phase was advanced via chat-service (the authority),
        // promoting the winner and removing the other jeebers.
        chat.AdvancePhaseCalls.Should().Be(1);
        chat.LastAdvancePhaseConversationId.Should().Be(requestId);
        chat.LastAdvancePhase!.Phase.Should().Be("accepted");
        chat.LastAdvancePhase.WinnerUserId.Should().Be("jeeber-kamal");
        chat.LastAdvancePhase.WinnerRoleInConvo.Should().Be("jeeber_winner");
        chat.LastAdvancePhase.RemoveOthers.Should().BeTrue();
    }

    [Fact]
    public async Task Accept_WhenPhaseAdvanceThrows_DefaultsPhaseToAccepted_StillReturns200()
    {
        // chat blip on phase advance must NEVER 5xx the accept; conversation_phase
        // defaults to "accepted" (the saga committed) so H7's assertion still holds.
        var chat = new RecordingJeebConversationClient
        {
            AdvancePhaseThrows = new JeebConversationApiException(HttpStatusCode.BadGateway, "boom"),
        };
        using var factory = NewFactory(chat, chatEnabled: true, offerEnabled: true,
            fakeOffer: AcceptedFake("offer-d2", "jeeber-win"));

        var requestId = await SeedRequestAsync(factory, "client-d2", "conv-d2-" + Guid.NewGuid());
        SeedRouting(factory, "offer-d2", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-d2").PostAsync("/offers/offer-d2/accept", null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["status"]!.Value<string>().Should().Be("accepted");
        json["winner_user_id"]!.Value<string>().Should().Be("jeeber-win");
        json["conversation_phase"]!.Value<string>().Should().Be("accepted");
    }

    // -----------------------------------------------------------------
    // D-WS (H6) — realtime gate mints a signed membership ticket for a member.
    // -----------------------------------------------------------------

    [Fact]
    public async Task RealtimeGate_Member_Returns200_With_SignedTicket_ScopedTo_Conversation_And_Viewer()
    {
        var chat = new RecordingJeebConversationClient
        {
            Membership = new JeebConversationMembership { IsMember = true, RoleInConvo = "jeeber_offerer" },
        };
        using var factory = NewRealtimeFactory(chat);
        var http = factory.CreateClient();
        var (token, viewerId) = await MintSession(http, "+9613009801");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-h6");
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["topic"]!.Value<string>().Should().Be("jeeb_conversation:conv-h6");

        var ticket = json["ticket"]!.Value<string>();
        ticket.Should().NotBeNullOrWhiteSpace("a member's WS join is authorized by a gateway-signed ticket");

        // The ticket is a JWT scoped to (conversation, viewer, role) — realtime
        // verifies it at join without calling chat-service.
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(ticket);
        jwt.Subject.Should().Be(viewerId);
        jwt.Claims.Should().Contain(c => c.Type == "conv" && c.Value == "conv-h6");
        jwt.Claims.Should().Contain(c => c.Type == "role" && c.Value == "jeeber_offerer");
        jwt.ValidTo.Should().BeAfter(DateTime.UtcNow);
    }

    [Fact]
    public async Task RealtimeGate_NonMember_Returns403_NoTicket()
    {
        var chat = new RecordingJeebConversationClient
        {
            Membership = new JeebConversationMembership { IsMember = false },
        };
        using var factory = NewRealtimeFactory(chat);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613009802");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-h6n");
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not_in_membership");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static FakeOfferServiceClient AcceptedFake(string offerId, string winningJeeberId)
        => new()
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = offerId,
                    JeeberId = winningJeeberId,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            },
        };

    private static WebApplicationFactory<Program> NewFactory(
        IJeebConversationClient chat,
        bool chatEnabled,
        bool offerEnabled = false,
        FakeOfferServiceClient? fakeOffer = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "FeatureFlags:UseUpstream:Chat", chatEnabled ? "true" : "false" },
                    { "FeatureFlags:UseUpstream:Offer", offerEnabled ? "true" : "false" },
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton(chat);
                if (fakeOffer is not null)
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(fakeOffer);
                }
            });
        });

    private static async Task<string> SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId, string? conversationId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
        }, default);
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            created.ConversationId = conversationId;
        }
        return created.Id;
    }

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    /// <summary>
    /// Factory for the realtime-gate tests: Chat on + a real OTP-minted bearer
    /// (the /v1/realtime gate is [Authorize]+ChatRead-gated, so it needs a real
    /// session bearer carrying sub==userId, not the X-User-Id dev header).
    /// </summary>
    private static WebApplicationFactory<Program> NewRealtimeFactory(IJeebConversationClient chat)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton(chat);

                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubServiceOtpClient());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Chat = true;
                    f.Otp = true;
                });
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });
            });
        });

    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        var token = json["accessToken"]!.Value<string>()!;
        var userId = json["user"]!["userId"]!.Value<string>()!;
        return (token, userId);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    /// <summary>
    /// Recording fake for chat-service's conversation aggregate — captures the
    /// add-participant / advance-phase / membership calls so the gateway's seating,
    /// phase-advance, and ticket-mint composition can be asserted deterministically.
    /// </summary>
    private sealed class RecordingJeebConversationClient : IJeebConversationClient
    {
        public JeebConversationMembership Membership { get; init; }
            = new() { IsMember = true, RoleInConvo = "client" };

        public JeebConversationApiException? AddParticipantThrows { get; init; }
        public JeebConversationApiException? AdvancePhaseThrows { get; init; }
        public string AdvancedPhase { get; init; } = "accepted";

        public int AddParticipantCalls { get; private set; }
        public string? LastAddParticipantConversationId { get; private set; }
        public AddJeebParticipantRequest? LastAddParticipant { get; private set; }

        public int AdvancePhaseCalls { get; private set; }
        public string? LastAdvancePhaseConversationId { get; private set; }
        public AdvanceJeebPhaseRequest? LastAdvancePhase { get; private set; }

        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
        {
            AddParticipantCalls++;
            LastAddParticipantConversationId = conversationId;
            LastAddParticipant = request;
            if (AddParticipantThrows is not null)
            {
                throw AddParticipantThrows;
            }
            return Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
            });
        }

        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
        {
            AdvancePhaseCalls++;
            LastAdvancePhaseConversationId = conversationId;
            LastAdvancePhase = request;
            if (AdvancePhaseThrows is not null)
            {
                throw AdvancePhaseThrows;
            }
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                CorrelationKey = conversationId,
                Phase = AdvancedPhase,
                Participants = new List<JeebConversationParticipant>(),
            });
        }

        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(Membership);

        // Unused by these tests — minimal stubs.
        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse());

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse());

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
            => Task.FromResult(new JeebMessageResponse());

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse());

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => Task.FromResult(new JeebMessageListResponse());
    }

    /// <summary>No-op OTP upstream so the verify path mints a real session (sub == userId).</summary>
    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Canned offer-service accept double (mirrors OfferAcceptOrchestrationTests).</summary>
    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Result);

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
