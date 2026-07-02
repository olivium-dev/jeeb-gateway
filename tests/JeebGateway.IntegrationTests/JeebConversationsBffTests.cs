using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Conversations.Client;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 (JEB-50/51/52/53) — end-to-end tests for the gateway's Jeeb CONVERSATION
/// BFF (<c>JeebConversationsController</c>), driven through the REAL host with a
/// REAL bearer (minted via the OTP verify path, sub == userId) and a FAKE
/// <see cref="IJeebConversationClient"/> standing in for chat-service's
/// conversation aggregate (which lands in a sequenced upstream PR).
///
/// These lock the BFF contract the S08 suite asserts body-strict:
///   • H1  POST /v1/chat/jeeb/conversations -> 201 {conversation_id, phase, correlation_key, participants[]}
///   • H3  POST /v1/conversations/{id}/messages -> 201 {message_id, kind, subtype, author_id} — author from BEARER
///   • H5  GET  /v1/conversations/{id}/messages -> 200 viewer-filtered (gateway forwards viewer, never filters)
///   • N1  GET  /v1/conversations/{id}/messages (non-member) -> 403 forwarded verbatim (never empty-200)
///   • N2  GET  /v1/realtime/jeeb:chat:{id} (non-member) -> 403 not_in_membership
///   • H6  GET  /v1/realtime/jeeb:chat:{id} (member) -> 200 channel descriptor (REST pre-check; socket not proxied)
///   • H2  GET  /v1/conversations?correlationKey={id} -> 200 {participants[], phase}
///   • flag-gate: every route -> 503 ProblemDetails while FeatureFlags:UseUpstream:Chat is off
///   • auth: missing bearer -> 401
///
/// The gateway computes NO visibility and holds NO conversation state — the fake
/// asserts the gateway forwards the viewer / stamps author_id from the bearer /
/// forwards the upstream status verbatim, nothing more.
/// </summary>
public sealed class JeebConversationsBffTests
{
    // ---------------------------------------------------------------------
    // H1 — create
    // ---------------------------------------------------------------------

    [Fact]
    public async Task H1_Create_Returns201_With_Conversation_Phase_Correlation_Participants()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001801");

        var requestId = "req-h1-" + userId;
        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/jeeb/conversations")
        {
            Content = JsonContent.Create(new { request_id = requestId, client_user_id = userId }),
        };
        msg.Headers.Authorization = Bearer(token);
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", requestId);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["conversation_id"]!.Value<string>().Should().NotBeNullOrWhiteSpace();
        json["phase"]!.Value<string>().Should().Be("broadcasting");
        json["correlation_key"]!.Value<string>().Should().Be(requestId);
        json["participants"]!.Should().HaveCount(1);
        json["participants"]![0]!["user_id"]!.Value<string>().Should().Be(userId);
        json["participants"]![0]!["role_in_convo"]!.Value<string>().Should().Be("client");
        json["participants"]![0]!["removed_at"]!.Type.Should().Be(JTokenType.Null);

        // The gateway forwarded the Idempotency-Key verbatim (== request_id).
        fake.LastCreate!.IdempotencyKey.Should().Be(requestId);
        fake.LastCreate.ClientUserId.Should().Be(userId);
    }

    [Fact]
    public async Task H1_Create_MissingFields_Returns400_ProblemDetails()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001802");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/jeeb/conversations")
        {
            Content = JsonContent.Create(new { request_id = "" }),
        };
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------------
    // H3 — append (author from bearer, never body)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task H3_AppendStructured_Returns201_AuthorFromBearer_NotBody()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, kamalUserId) = await MintSession(http, "+9613001803");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/conv-1/messages")
        {
            // The caller TRIES to spoof author_id in the body — it MUST be ignored.
            Content = JsonContent.Create(new
            {
                kind = "structured",
                subtype = "jeeb.offer",
                author_id = "SPOOFED-attacker",
                payload = new { offerId = "off-1", priceUsd = 35, etaMinutes = 25, note = "On my way" },
            }),
        };
        msg.Headers.Authorization = Bearer(token);
        msg.Headers.TryAddWithoutValidation("Idempotency-Key", "idem-h3-1");

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["kind"]!.Value<string>().Should().Be("structured");
        json["subtype"]!.Value<string>().Should().Be("jeeb.offer");
        json["author_id"]!.Value<string>().Should().Be(kamalUserId);
        json["message_id"]!.Value<string>().Should().NotBeNullOrWhiteSpace();

        // SECURITY: the gateway stamped the bearer sub, NOT the spoofed body value.
        fake.LastAppend!.AuthorId.Should().Be(kamalUserId);
        fake.LastAppend.AuthorId.Should().NotBe("SPOOFED-attacker");
        fake.LastAppendConversationId.Should().Be("conv-1");
        fake.LastAppend.IdempotencyKey.Should().Be("idem-h3-1");
    }

    [Fact]
    public async Task H4_AppendResponse_EchoesTheJustPostedMessage_AudienceAll_BodyAuthor_NotForeign()
    {
        // H4 regression: the append RESPONSE must be the message created for THIS
        // request — correct author (bearer sub), kind, audience("all"), body —
        // round-tripping the open audience/payload shapes faithfully across the
        // STJ-bind -> Newtonsoft-wire -> STJ-out serializer hops, NEVER a foreign /
        // mangled (audience=null, {"ValueKind":N}) message.
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, samiUserId) = await MintSession(http, "+9613001830");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/conv-h4/messages")
        {
            Content = JsonContent.Create(new
            {
                kind = "text",
                audience = "all",
                body = "On my way",
            }),
        };
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("ValueKind", "the audience must echo as a value, never the JsonElement struct shape");
        var json = JObject.Parse(raw);

        // The response echoes the just-posted message verbatim.
        json["kind"]!.Value<string>().Should().Be("text");
        json["body"]!.Value<string>().Should().Be("On my way");
        json["audience"]!.Value<string>().Should().Be("all");
        // author is the BEARER sub (sami), not kamal/a foreign author.
        json["author_id"]!.Value<string>().Should().Be(samiUserId);

        // The gateway forwarded a FAITHFUL audience JsonElement to chat-service
        // (the value "all"), not a mangled struct.
        fake.LastAppend!.Audience!.Value.GetString().Should().Be("all");
        fake.LastAppend.Body.Should().Be("On my way");
    }

    [Fact]
    public async Task H4_AppendStructuredResponse_EchoesPayloadVerbatim()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, kamalUserId) = await MintSession(http, "+9613001831");

        var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/conv-h4b/messages")
        {
            Content = JsonContent.Create(new
            {
                kind = "structured",
                subtype = "jeeb.offer",
                audience = "all",
                payload = new { offerId = "off-1", priceUsd = 35, etaMinutes = 25 },
            }),
        };
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("ValueKind");
        var json = JObject.Parse(raw);
        json["subtype"]!.Value<string>().Should().Be("jeeb.offer");
        json["author_id"]!.Value<string>().Should().Be(kamalUserId);
        json["audience"]!.Value<string>().Should().Be("all");
        json["payload"]!["offerId"]!.Value<string>().Should().Be("off-1");
        json["payload"]!["priceUsd"]!.Value<int>().Should().Be(35);
        json["payload"]!["etaMinutes"]!.Value<int>().Should().Be(25);
    }

    // ---------------------------------------------------------------------
    // H5 / N1 — viewer-filtered read; gateway forwards viewer, never filters
    // ---------------------------------------------------------------------

    [Fact]
    public async Task H5_ListMessages_Returns200_And_ForwardsBearerViewer()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, kamalUserId) = await MintSession(http, "+9613001804");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-1/messages");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The gateway forwarded the BEARER as the viewer (chat-service owns the filter).
        fake.LastListViewer.Should().Be(kamalUserId);
        fake.LastListConversationId.Should().Be("conv-1");
    }

    [Fact]
    public async Task N1_NonMemberRead_Forwards403_VerbatimFromChatService_NotEmpty200()
    {
        // chat-service denies a non-member with 403 at the membership gate; the
        // gateway forwards it verbatim — never an empty 200 list (INV-2).
        var fake = new FakeJeebConversationClient
        {
            ListThrows = new JeebConversationApiException(HttpStatusCode.Forbidden, "not_in_membership"),
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001805");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-1/messages");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("\"messages\":");
    }

    // ---------------------------------------------------------------------
    // A6 — viewer-filtered DELTA read (messages since a cursor)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task A6_ListMessagesSince_Returns200_And_ForwardsViewerAndCursor()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, kamalUserId) = await MintSession(http, "+9613001820");

        var msg = new HttpRequestMessage(
            HttpMethod.Get, "/v1/conversations/conv-1/messages/since/cursor-42");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The gateway forwarded the BEARER as viewer + the cursor verbatim; the
        // delta path filters via the SAME chat-service VisibilityFilter (parity).
        fake.LastSinceViewer.Should().Be(kamalUserId);
        fake.LastSinceConversationId.Should().Be("conv-1");
        fake.LastSinceCursor.Should().Be("cursor-42");
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["messages"]!.Should().HaveCount(1);
    }

    [Fact]
    public async Task A6_NonMemberDeltaRead_Forwards403_VerbatimFromChatService()
    {
        // A non-member delta read is denied at chat-service's membership gate; the
        // gateway forwards the 403 verbatim (no leak via the delta path, INV-1).
        var fake = new FakeJeebConversationClient
        {
            SinceThrows = new JeebConversationApiException(HttpStatusCode.Forbidden, "not_in_membership"),
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001821");

        var msg = new HttpRequestMessage(
            HttpMethod.Get, "/v1/conversations/conv-1/messages/since/cursor-42");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("\"messages\":");
    }

    [Fact]
    public async Task A6_DeltaRead_FlagOff_Returns503_DoesNotDialChatService()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: false); // flag OFF
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001822");

        var msg = new HttpRequestMessage(
            HttpMethod.Get, "/v1/conversations/conv-1/messages/since/cursor-42");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.SinceCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // N2 / H6 — realtime visibility gate
    // ---------------------------------------------------------------------

    [Fact]
    public async Task N2_NonMember_RealtimeGate_Returns403_NotInMembership()
    {
        var fake = new FakeJeebConversationClient
        {
            Membership = new JeebConversationMembership { IsMember = false },
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, outsiderUserId) = await MintSession(http, "+9613001806");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-1");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("not_in_membership");
        // The gateway asked chat-service about the BEARER viewer.
        fake.LastMembershipViewer.Should().Be(outsiderUserId);
        fake.LastMembershipConversationId.Should().Be("conv-1");
    }

    [Fact]
    public async Task N2_RemovedMember_RealtimeGate_Returns403_FailClosed()
    {
        // A removed participant (removed_at set) may still REST-read up-to-cutoff
        // history, but the LIVE socket is for active members only -> 403.
        var fake = new FakeJeebConversationClient
        {
            Membership = new JeebConversationMembership
            {
                IsMember = true,
                RoleInConvo = "jeeber_offerer",
                RemovedAt = System.DateTimeOffset.UtcNow,
            },
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001807");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-1");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("not_in_membership");
    }

    [Fact]
    public async Task H6_Member_RealtimeGate_Returns200_ChannelDescriptor()
    {
        var fake = new FakeJeebConversationClient
        {
            Membership = new JeebConversationMembership { IsMember = true, RoleInConvo = "client" },
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001808");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-1");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        // The descriptor maps the suite topic jeeb:chat:{id} -> realtime jeeb_conversation:{id}.
        json["topic"]!.Value<string>().Should().Be("jeeb_conversation:conv-1");
        json["conversationId"]!.Value<string>().Should().Be("conv-1");
    }

    // ---------------------------------------------------------------------
    // H2 — membership read by correlation
    // ---------------------------------------------------------------------

    [Fact]
    public async Task H2_GetByCorrelation_Returns200_Participants_And_Phase()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001809");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations?correlationKey=req-h2");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        json["phase"]!.Value<string>().Should().Be("broadcasting");
        json["participants"]!.Should().NotBeNull();
        fake.LastCorrelationKey.Should().Be("req-h2");
    }

    [Fact]
    public async Task GetByCorrelation_MissingKey_Returns400()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001810");

        var msg = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------------------------------------------------------------------
    // PR-G3 — by-request alias (the route the mobile client calls today, which 404'd)
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ByRequest_Alias_Returns_Same_Body_As_CorrelationKey_Read()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001840");

        const string requestId = "req-alias-1";

        // The mobile-facing alias.
        var aliasMsg = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/chat/jeeb/conversations/by-request/{requestId}");
        aliasMsg.Headers.Authorization = Bearer(token);
        var aliasResp = await http.SendAsync(aliasMsg);

        aliasResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var aliasJson = JObject.Parse(await aliasResp.Content.ReadAsStringAsync());

        // The existing correlation-key read for the SAME request id.
        var corrMsg = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/conversations?correlationKey={requestId}");
        corrMsg.Headers.Authorization = Bearer(token);
        var corrResp = await http.SendAsync(corrMsg);
        var corrJson = JObject.Parse(await corrResp.Content.ReadAsStringAsync());

        // Byte-equivalent body: same conversation, same phase, same correlation key.
        aliasJson["conversation_id"]!.Value<string>()
            .Should().Be(corrJson["conversation_id"]!.Value<string>());
        aliasJson["phase"]!.Value<string>().Should().Be(corrJson["phase"]!.Value<string>());
        aliasJson["correlation_key"]!.Value<string>().Should().Be(requestId);

        // The gateway delegated to the SAME correlation-key read, forwarding requestId.
        fake.LastCorrelationKey.Should().Be(requestId);
    }

    [Fact]
    public async Task ByRequest_Alias_Unknown_Request_Forwards404_Verbatim()
    {
        var fake = new FakeJeebConversationClient
        {
            CorrelationThrows = new JeebConversationApiException(HttpStatusCode.NotFound, "conversation_not_found"),
        };
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001841");

        var msg = new HttpRequestMessage(
            HttpMethod.Get, "/v1/chat/jeeb/conversations/by-request/req-unknown");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an unknown request's conversation 404s — the upstream status is forwarded verbatim");
    }

    [Fact]
    public async Task ByRequest_Alias_FlagOff_Returns503_DoesNotDialChatService()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: false); // flag OFF
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001842");

        var msg = new HttpRequestMessage(
            HttpMethod.Get, "/v1/chat/jeeb/conversations/by-request/req-x");
        msg.Headers.Authorization = Bearer(token);

        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        fake.LastCorrelationKey.Should().BeNull("flag off must not dial chat-service");
    }

    // ---------------------------------------------------------------------
    // flag-gate + auth
    // ---------------------------------------------------------------------

    [Fact]
    public async Task FlagOff_AllRoutes_Return503_ProblemDetails()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: false); // flag OFF
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001811");

        var create = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/jeeb/conversations")
        {
            Content = JsonContent.Create(new { request_id = "r", client_user_id = userId }),
        };
        create.Headers.Authorization = Bearer(token);
        (await http.SendAsync(create)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var read = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-1/messages");
        read.Headers.Authorization = Bearer(token);
        (await http.SendAsync(read)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var gate = new HttpRequestMessage(HttpMethod.Get, "/v1/realtime/jeeb:chat:conv-1");
        gate.Headers.Authorization = Bearer(token);
        (await http.SendAsync(gate)).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // Flag off must NOT have dialed chat-service.
        fake.CreateCalls.Should().Be(0);
        fake.ListCalls.Should().Be(0);
        fake.MembershipCalls.Should().Be(0);
    }

    [Fact]
    public async Task MissingBearer_Returns401()
    {
        var fake = new FakeJeebConversationClient();
        using var factory = MakeFactory(fake, chatEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.GetAsync("/v1/conversations/conv-1/messages");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private const string AppId = "jeeb-test-app";

    private static WebApplicationFactory<Program> MakeFactory(
        IJeebConversationClient fake, bool chatEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton(fake);

                // Stub the OTP upstream so MintSession can verify a real session
                // (the bearer carries sub == userId, which the BFF reads).
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubServiceOtpClient());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Chat = chatEnabled;
                    f.Otp = true; // enable the OTP sign-in path used to mint the bearer
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    /// <summary>Mints a real session via the OTP verify path; returns (accessToken, userId == sub).</summary>
    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        var token = json["accessToken"]!.Value<string>()!;
        var userId = json["user"]!["userId"]!.Value<string>()!;
        token.Should().NotBeNullOrWhiteSpace();
        userId.Should().NotBeNullOrWhiteSpace();
        return (token, userId);
    }

    /// <summary>
    /// In-memory stand-in for chat-service's conversation aggregate. Records the
    /// last call args (so tests assert the gateway forwarded viewer / stamped
    /// author from bearer / forwarded the key) and can be told to throw a specific
    /// upstream status (so the 403-forward path is exercised).
    /// </summary>
    private sealed class FakeJeebConversationClient : IJeebConversationClient
    {
        public CreateJeebConversationRequest? LastCreate { get; private set; }
        public AppendJeebMessageRequest? LastAppend { get; private set; }
        public string? LastAppendConversationId { get; private set; }
        public string? LastListConversationId { get; private set; }
        public string? LastListViewer { get; private set; }
        public string? LastMembershipConversationId { get; private set; }
        public string? LastMembershipViewer { get; private set; }
        public string? LastCorrelationKey { get; private set; }

        public int CreateCalls { get; private set; }
        public int ListCalls { get; private set; }
        public int MembershipCalls { get; private set; }

        public JeebConversationApiException? ListThrows { get; init; }

        /// <summary>PR-G3: when set, GetConversationByCorrelationAsync throws it (404-forward path).</summary>
        public JeebConversationApiException? CorrelationThrows { get; init; }

        public JeebConversationMembership Membership { get; init; }
            = new() { IsMember = true, RoleInConvo = "client" };

        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
        {
            CreateCalls++;
            LastCreate = request;
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + request.RequestId,
                CorrelationKey = request.RequestId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>
                {
                    new()
                    {
                        UserId = request.ClientUserId,
                        RoleInConvo = "client",
                        RemovedAt = null,
                    },
                },
            });
        }

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
        {
            LastCorrelationKey = correlationKey;
            if (CorrelationThrows is not null)
            {
                throw CorrelationThrows;
            }
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + correlationKey,
                CorrelationKey = correlationKey,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });
        }

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
        {
            LastAppendConversationId = conversationId;
            LastAppend = request;
            return Task.FromResult(new JeebMessageResponse
            {
                MessageId = "msg-1",
                Kind = request.Kind,
                Subtype = request.Subtype,
                AuthorId = request.AuthorId,
                Audience = request.Audience,
                Payload = request.Payload,
                Body = request.Body,
            });
        }

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
        {
            ListCalls++;
            LastListConversationId = conversationId;
            LastListViewer = viewerUserId;
            if (ListThrows is not null)
            {
                throw ListThrows;
            }
            return Task.FromResult(new JeebMessageListResponse
            {
                Messages = new List<JeebMessageResponse>
                {
                    new() { MessageId = "msg-1", Kind = "text", Body = "hi" },
                },
            });
        }

        public string? LastSinceConversationId { get; private set; }
        public string? LastSinceViewer { get; private set; }
        public string? LastSinceCursor { get; private set; }
        public int SinceCalls { get; private set; }
        public JeebConversationApiException? SinceThrows { get; init; }

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
        {
            SinceCalls++;
            LastSinceConversationId = conversationId;
            LastSinceViewer = viewerUserId;
            LastSinceCursor = cursor;
            if (SinceThrows is not null)
            {
                throw SinceThrows;
            }
            return Task.FromResult(new JeebMessageListResponse
            {
                Messages = new List<JeebMessageResponse>
                {
                    new() { MessageId = "msg-delta-1", Kind = "text", Body = "delta" },
                },
            });
        }

        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
        {
            MembershipCalls++;
            LastMembershipConversationId = conversationId;
            LastMembershipViewer = viewerUserId;
            return Task.FromResult(Membership);
        }

        public AddJeebParticipantRequest? LastAddParticipant { get; private set; }
        public string? LastAddParticipantConversationId { get; private set; }
        public int AddParticipantCalls { get; private set; }

        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
        {
            AddParticipantCalls++;
            LastAddParticipantConversationId = conversationId;
            LastAddParticipant = request;
            return Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
                RemovedAt = null,
            });
        }

        public AdvanceJeebPhaseRequest? LastAdvancePhase { get; private set; }
        public string? LastAdvancePhaseConversationId { get; private set; }
        public int AdvancePhaseCalls { get; private set; }

        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
        {
            AdvancePhaseCalls++;
            LastAdvancePhaseConversationId = conversationId;
            LastAdvancePhase = request;
            return Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                CorrelationKey = conversationId,
                Phase = request.Phase,
                Participants = new List<JeebConversationParticipant>(),
            });
        }
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
}
