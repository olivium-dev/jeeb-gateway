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
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P45 (chat attachments, P0) — the MISSING contract test the on-device validation
/// flagged: a message posted with a STRUCTURED payload must come back
/// byte-equivalent on the GET round-trip, not as a JsonElement/JObject struct husk.
///
/// <para>
/// THE FIELD SYMPTOM. The client posts
/// <c>{"kind":"image","payload":{"url":"chat_attachment/….jpg"}}</c> and a later
/// <c>GET /v1/conversations/{id}/messages</c> answers
/// <c>{"kind":"image","payload":{"valueKind":1}}</c> — <c>valueKind</c> being the
/// single public CLR property of <see cref="System.Text.Json.JsonElement"/>
/// (<c>JsonValueKind.Object == 1</c>). The object_ref is gone, so the image renders
/// as a grey placeholder on refetch while the sender's optimistic render masks it.
/// The husk is minted by whichever hop REFLECTS over a JsonElement (or a Newtonsoft
/// JObject) instead of writing its RAW JSON — the classic two-JSON-stack marshaling
/// bug (S08 H4, see <see cref="RawJsonElementConverter"/>).
/// </para>
///
/// <para>
/// WHAT THIS SUITE LOCKS. The gateway's own two legs, exercised through the REAL
/// host with a REAL bearer:
/// <list type="number">
///   <item>REQUEST leg — the exact bytes <see cref="JeebConversationClient"/> puts
///   on the chat-service wire (<c>JsonConvert.SerializeObject(request)</c>) must
///   carry <c>payload</c> verbatim.</item>
///   <item>RESPONSE leg — chat-service's body read back with
///   <c>JsonConvert.DeserializeObject&lt;T&gt;</c> and re-emitted by the ASP.NET
///   System.Text.Json response serializer must still carry <c>payload</c> verbatim,
///   on BOTH the append 201 and the GET 200.</item>
/// </list>
/// The fake upstream deliberately marshals through the SAME two
/// <c>JsonConvert</c> calls the live client performs and stores the resulting wire
/// JSON as text (exactly as chat-service persists its opaque payload column), so a
/// regression on either serializer leg — a dropped
/// <see cref="RawJsonElementConverter"/>, a property retyped to <c>object</c>,
/// a switch of either stack — fails HERE instead of on a phone.
/// </para>
///
/// Covers <c>kind=image</c> (the P45 attachment shape) and <c>kind=text</c> (which
/// must stay byte-identical and payload-free).
///
/// <para>
/// SCOPE — READ THIS BEFORE TREATING A GREEN RUN AS "P45 IS FIXED". This suite is a
/// REGRESSION PIN ON THE GATEWAY'S OWN LEGS, not proof that chat attachments render.
/// It passes on <c>origin/main</c> with no product change, because the gateway legs
/// were already correct (<c>RawJsonElementConverter</c>, commit da208cd). The P45 P0
/// lives in chat-service and is reproduced by curl with the gateway out of the path
/// — see <see cref="Gateway_Relays_An_Upstream_Husk_Verbatim_TheDefectIsUpstream"/>,
/// which pins the honest gateway behaviour: when chat-service hands it a husk, the
/// gateway forwards that husk unchanged. It has no payload to repair.
/// </para>
/// </summary>
public sealed class P45ChatAttachmentPayloadRoundTripTests
{
    /// <summary>The exact attachment payload shape the mobile client sends.</summary>
    private const string ImagePayloadJson =
        "{\"url\":\"chat_attachment/06b882b1-2f0a-4a2e-9d3c-8f5a1b7e4c10.jpg\","
        + "\"mimeType\":\"image/jpeg\",\"width\":1080,\"height\":1440,\"sizeBytes\":284133}";

    // ---------------------------------------------------------------------
    // kind = image — the P0 shape
    // ---------------------------------------------------------------------

    [Fact]
    public async Task P45_ImageMessage_Payload_Survives_The_Post_Then_Get_RoundTrip()
    {
        var upstream = new WireFaithfulConversationUpstream();
        using var factory = MakeFactory(upstream, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001860");

        // --- POST the attachment message -------------------------------------
        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/conv-p45/messages")
        {
            Content = new StringContent(
                "{\"kind\":\"image\",\"audience\":\"all\",\"payload\":" + ImagePayloadJson + "}",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        post.Headers.Authorization = Bearer(token);
        var postResp = await http.SendAsync(post);

        postResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var postRaw = await postResp.Content.ReadAsStringAsync();
        AssertNoStructHusk(postRaw);

        var posted = JObject.Parse(postRaw);
        posted["kind"]!.Value<string>().Should().Be("image");
        posted["author_id"]!.Value<string>().Should().Be(userId);
        AssertPayloadIsVerbatim(posted["payload"], "the append 201 must echo the attachment payload");

        // --- the bytes the gateway actually put on the chat-service wire ------
        upstream.LastRequestWire.Should().NotBeNull();
        var wire = JObject.Parse(upstream.LastRequestWire!);
        AssertPayloadIsVerbatim(
            wire["payload"],
            "the gateway must forward the payload as raw JSON, never as the JsonElement struct");
        AssertNoStructHusk(upstream.LastRequestWire!);
        wire["kind"]!.Value<string>().Should().Be("image");

        // --- GET the conversation back (the leg that died in the field) -------
        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-p45/messages");
        get.Headers.Authorization = Bearer(token);
        var getResp = await http.SendAsync(get);

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRaw = await getResp.Content.ReadAsStringAsync();
        AssertNoStructHusk(getRaw);

        var messages = JObject.Parse(getRaw)["messages"]!;
        messages.Should().HaveCount(1);
        messages[0]!["kind"]!.Value<string>().Should().Be("image");
        AssertPayloadIsVerbatim(
            messages[0]!["payload"],
            "the refetched message must still carry the object_ref — this is the P45 P0");

        // The single load-bearing field, called out explicitly: the object ref.
        messages[0]!["payload"]!["url"]!.Value<string>()
            .Should().Be("chat_attachment/06b882b1-2f0a-4a2e-9d3c-8f5a1b7e4c10.jpg");
    }

    // ---------------------------------------------------------------------
    // kind = text — must be untouched by the payload plumbing
    // ---------------------------------------------------------------------

    [Fact]
    public async Task P45_TextMessage_RoundTrips_Unchanged_With_No_Payload_Husk()
    {
        var upstream = new WireFaithfulConversationUpstream();
        using var factory = MakeFactory(upstream, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001861");

        var post = new HttpRequestMessage(HttpMethod.Post, "/v1/conversations/conv-p45t/messages")
        {
            Content = JsonContent.Create(new { kind = "text", audience = "all", body = "On my way 🚗" }),
        };
        post.Headers.Authorization = Bearer(token);
        var postResp = await http.SendAsync(post);

        postResp.StatusCode.Should().Be(HttpStatusCode.Created);
        AssertNoStructHusk(await postResp.Content.ReadAsStringAsync());

        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-p45t/messages");
        get.Headers.Authorization = Bearer(token);
        var getResp = await http.SendAsync(get);

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRaw = await getResp.Content.ReadAsStringAsync();
        AssertNoStructHusk(getRaw);

        var message = JObject.Parse(getRaw)["messages"]![0]!;
        message["kind"]!.Value<string>().Should().Be("text");
        message["body"]!.Value<string>().Should().Be("On my way 🚗",
            "a text message's body must round-trip byte-identical");
        message["author_id"]!.Value<string>().Should().Be(userId);
        message["audience"]!.Value<string>().Should().Be("all");
        (message["payload"] is null || message["payload"]!.Type == JTokenType.Null)
            .Should().BeTrue("a text message carries no payload — and must not grow a struct husk");
    }

    // ---------------------------------------------------------------------
    // Mixed conversation — an image between two texts stays intact
    // ---------------------------------------------------------------------

    [Fact]
    public async Task P45_MixedConversation_ImageBetweenTexts_AllSurvive_The_Refetch()
    {
        var upstream = new WireFaithfulConversationUpstream();
        using var factory = MakeFactory(upstream, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001862");

        await Post(http, token, "conv-p45m", "{\"kind\":\"text\",\"audience\":\"all\",\"body\":\"before\"}");
        await Post(http, token, "conv-p45m",
            "{\"kind\":\"image\",\"audience\":\"all\",\"payload\":" + ImagePayloadJson + "}");
        await Post(http, token, "conv-p45m", "{\"kind\":\"text\",\"audience\":\"all\",\"body\":\"after\"}");

        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-p45m/messages");
        get.Headers.Authorization = Bearer(token);
        var getResp = await http.SendAsync(get);

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var getRaw = await getResp.Content.ReadAsStringAsync();
        AssertNoStructHusk(getRaw);

        var messages = JObject.Parse(getRaw)["messages"]!;
        messages.Should().HaveCount(3);
        messages[0]!["body"]!.Value<string>().Should().Be("before");
        messages[2]!["body"]!.Value<string>().Should().Be("after");
        AssertPayloadIsVerbatim(messages[1]!["payload"], "the attachment between two texts must survive");
    }

    // ---------------------------------------------------------------------
    // The upstream defect, documented from the gateway's side
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Gateway_Relays_An_Upstream_Husk_Verbatim_TheDefectIsUpstream()
    {
        // WHERE P45 ACTUALLY BREAKS. chat-service is AddNewtonsoftJson
        // (ChatService.API/Program.cs) whose default resolver is camelCase, while
        // ConversationService.DeserializePayload hands its `object? Payload` a
        // System.Text.Json JsonElement — so its response serializer reflects over the
        // struct and emits {"payload":{"valueKind":1}}. Confirmed by curl straight at
        // chat-service on MSI with the gateway out of the path entirely.
        //
        // This test pins the gateway's HONEST behaviour in that world: it is a
        // faithful pipe, so it relays the husk unchanged. It does not crash, does not
        // silently drop the message, and — critically — CANNOT repair a payload that
        // reached it already destroyed. A green run of the tests above therefore says
        // "the gateway legs are intact", never "attachments render".
        var upstream = new WireFaithfulConversationUpstream { EmitUpstreamHusk = true };
        using var factory = MakeFactory(upstream, chatEnabled: true);
        var http = factory.CreateClient();
        var (token, _) = await MintSession(http, "+9613001863");

        await Post(http, token, "conv-p45u",
            "{\"kind\":\"image\",\"audience\":\"all\",\"payload\":" + ImagePayloadJson + "}");

        // The gateway put the payload on the upstream wire FAITHFULLY …
        AssertPayloadIsVerbatim(
            JObject.Parse(upstream.LastRequestWire!)["payload"],
            "the gateway's request leg is correct even when the upstream is not");

        // … and chat-service still answered with the husk, which the gateway relays.
        var get = new HttpRequestMessage(HttpMethod.Get, "/v1/conversations/conv-p45u/messages");
        get.Headers.Authorization = Bearer(token);
        var getResp = await http.SendAsync(get);

        getResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = JObject.Parse(await getResp.Content.ReadAsStringAsync())["messages"]![0]!["payload"]!;
        payload["valueKind"]!.Value<int>().Should().Be(1,
            "the gateway forwards what chat-service said — the husk is minted upstream, "
            + "so the P45 fix belongs in chat-service, not here");
        payload["url"].Should().BeNull("the object_ref was already gone when it reached the gateway");
    }

    // ---------------------------------------------------------------------
    // assertions
    // ---------------------------------------------------------------------

    /// <summary>
    /// The payload must be DEEP-EQUAL to what was posted — every key, every value,
    /// no reshaping. This is the "byte-equivalent" bar the validation asked for
    /// (compared as canonical JSON so key order/whitespace are not load-bearing).
    /// </summary>
    private static void AssertPayloadIsVerbatim(JToken? actual, string because)
    {
        actual.Should().NotBeNull(because);
        actual!.Type.Should().Be(JTokenType.Object,
            "the payload must stay a JSON object, not collapse to a scalar/struct — " + because);
        JToken.DeepEquals(JObject.Parse(ImagePayloadJson), actual)
            .Should().BeTrue(
                because + " (expected {0}, got {1})",
                ImagePayloadJson,
                actual.ToString(Formatting.None));
    }

    /// <summary>
    /// No hop may reflect over a JsonElement / JObject. Case-insensitive because the
    /// husk surfaces as <c>ValueKind</c> (Newtonsoft default naming) or
    /// <c>valueKind</c> (camelCase naming) depending on which stack mangled it.
    /// </summary>
    private static void AssertNoStructHusk(string json)
    {
        json.ToLowerInvariant().Should().NotContain("valuekind",
            "a JsonElement's struct shape must never reach the wire — that IS the P45 husk");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static async Task Post(HttpClient http, string token, string conversationId, string bodyJson)
    {
        var msg = new HttpRequestMessage(HttpMethod.Post, $"/v1/conversations/{conversationId}/messages")
        {
            Content = new StringContent(bodyJson, System.Text.Encoding.UTF8, "application/json"),
        };
        msg.Headers.Authorization = Bearer(token);
        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

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

                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new NoopOtpClient());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Chat = chatEnabled;
                    f.Otp = true;
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
        return (json["accessToken"]!.Value<string>()!, json["user"]!["userId"]!.Value<string>()!);
    }

    /// <summary>
    /// A chat-service stand-in that is FAITHFUL TO THE SERIALIZERS, not just to the
    /// object graph. It performs the exact two marshaling steps
    /// <see cref="JeebConversationClient"/> performs against the live upstream:
    /// <c>JsonConvert.SerializeObject(request)</c> for the outbound body, and
    /// <c>JsonConvert.DeserializeObject&lt;T&gt;(body)</c> for the inbound one — and
    /// in between it keeps the message as JSON TEXT, mirroring how chat-service
    /// persists the payload as an opaque string column.
    ///
    /// <para>
    /// That fidelity is the whole point: a plain object-graph fake would hand the
    /// same CLR <c>JsonElement</c> straight back and could never observe a broken
    /// serializer leg, which is exactly how the P45 husk shipped unnoticed.
    /// </para>
    /// </summary>
    private sealed class WireFaithfulConversationUpstream : IJeebConversationClient
    {
        private readonly Dictionary<string, List<string>> _stored = new();

        /// <summary>The raw body the gateway would POST to chat-service, last call.</summary>
        public string? LastRequestWire { get; private set; }

        /// <summary>
        /// When set, the upstream answers the way the LIVE chat-service does today:
        /// a boxed System.Text.Json JsonElement reflected over by a camelCase
        /// Newtonsoft resolver, i.e. the literal husk {"valueKind":1}.
        /// </summary>
        public bool EmitUpstreamHusk { get; init; }

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
        {
            // LEG 1 — the gateway -> chat-service wire (JeebConversationClient.JsonContent).
            var wire = JsonConvert.SerializeObject(request);
            LastRequestWire = wire;

            // chat-service stores the envelope opaquely and projects it back with an id.
            var envelope = JObject.Parse(wire);
            envelope.Remove("idempotency_key");
            var bucket = Bucket(conversationId);
            envelope["message_id"] = $"msg-{conversationId}-{bucket.Count + 1}";
            if (EmitUpstreamHusk && envelope["payload"] is { Type: not JTokenType.Null })
            {
                envelope["payload"] = new JObject { ["valueKind"] = 1 };
            }
            var chatServiceBody = envelope.ToString(Formatting.None);
            bucket.Add(chatServiceBody);

            // LEG 2 — chat-service -> gateway (JeebConversationClient.SendAsync).
            return Task.FromResult(
                JsonConvert.DeserializeObject<JeebMessageResponse>(chatServiceBody)!);
        }

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
        {
            var body = "{\"messages\":[" + string.Join(",", Bucket(conversationId)) + "]}";
            return Task.FromResult(
                JsonConvert.DeserializeObject<JeebMessageListResponse>(body)!);
        }

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => ListMessagesForViewerAsync(conversationId, viewerUserId, ct);

        private List<string> Bucket(string conversationId)
        {
            if (!_stored.TryGetValue(conversationId, out var bucket))
            {
                bucket = new List<string>();
                _stored[conversationId] = bucket;
            }
            return bucket;
        }

        // --- surface not exercised here -----------------------------------

        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + request.RequestId,
                CorrelationKey = request.RequestId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = "conv-" + correlationKey,
                CorrelationKey = correlationKey,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebConversationMembership> GetMembershipAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(new JeebConversationMembership { IsMember = true, RoleInConvo = "client" });

        public Task<JeebConversationParticipant> AddParticipantAsync(
            string conversationId, AddJeebParticipantRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationParticipant
            {
                UserId = request.UserId,
                RoleInConvo = request.RoleInConvo,
            });

        // GW5 / W1.6-gateway: chat-service's additive seat-and-settle route. This fake
        // stands in for a surface that does NOT drive the post-accept path, so a call
        // landing here is a wiring mistake — throw rather than return a default envelope
        // that would let a mis-wired test pass on an empty conversation.
        public Task<JeebConversationSettleResponse> SettleAsync(
            string conversationId, SettleJeebConversationRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<JeebConversationResponse> AdvancePhaseAsync(
            string conversationId, AdvanceJeebPhaseRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = conversationId,
                CorrelationKey = conversationId,
                Phase = request.Phase,
                Participants = new List<JeebConversationParticipant>(),
            });
    }

    /// <summary>No-op OTP upstream so the verify path mints a real session (sub == userId).</summary>
    private sealed class NoopOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
