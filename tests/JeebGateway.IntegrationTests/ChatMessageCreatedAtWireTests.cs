using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
using Stj = System.Text.Json;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// THE MESSAGE TIMESTAMP MUST SURVIVE THE GATEWAY. Root-cause pin for the
/// bilateral empty-thread defect: chat-service stamps <c>created_at</c> on every
/// message, the mobile history decoder looks for it first, and the gateway's typed
/// hop silently deleted it — so <c>GET /v1/conversations/{id}/messages</c> answered
/// 200 with the whole thread and the device rendered ZERO messages, for BOTH
/// participants.
///
/// <para>
/// WHY THE PREVIOUS SUITE MISSED IT. A dropped field is the quietest possible
/// failure on the sending side: nothing throws, the status is 200, the message
/// COUNT is correct, and every field the gateway's own tests asserted
/// (<c>message_id</c>, <c>author_id</c>, <c>kind</c>, <c>payload</c>) was present.
/// Only the receiver noticed. The lesson encoded here: a relay's contract test must
/// assert the FULL field set the upstream emits, not the subset this repo happens
/// to care about.
/// </para>
///
/// <para>
/// FIDELITY. <see cref="ReplayingConversationUpstream"/> does not build an object
/// graph. It replays chat-service response bodies as JSON TEXT through the exact
/// inbound marshaling step the live client performs
/// (<c>JsonConvert.DeserializeObject&lt;T&gt;</c> — see
/// <c>JeebConversationClient.SendAsync</c>), and the assertions read the bytes the
/// REAL ASP.NET System.Text.Json response serializer put on the wire. Both
/// serializer legs are therefore under test, which is the only way a dropped
/// property is observable at all — a plain object-graph fake hands the same CLR
/// instance back and can never see it.
/// </para>
///
/// <para>
/// The replayed bodies below are VERBATIM captures from the LIVE chat-service on
/// the dev host (<c>GET /api/conversations/{id}/messages?viewer=…</c>), ids
/// replaced with stable test literals. The timestamp text — ISO-8601 UTC with a
/// trailing <c>Z</c> and sub-second precision — is exactly what the live service
/// emits, so this suite pins the real wire format, not an invented one.
/// </para>
/// </summary>
public sealed class ChatMessageCreatedAtWireTests
{
    /// <summary>
    /// The timestamp text the LIVE chat-service emits (captured shape:
    /// <c>"created_at":"2026-07-27T13:06:01.817954Z"</c>).
    /// </summary>
    private const string LiveCreatedAt = "2026-07-27T13:06:01.817954Z";

    private const string ConversationId = "conv-createdat";

    /// <summary>
    /// A single-message list body in the LIVE chat-service shape — every field the
    /// service actually emits, in its order, including the envelope's
    /// <c>conversation_id</c> / <c>viewer_id</c>.
    /// </summary>
    private static string LiveListBody(string viewerId) =>
        $$"""
        {"conversation_id":"{{ConversationId}}","viewer_id":"{{viewerId}}","messages":[
          {"message_id":"msg-1","conversation_id":"{{ConversationId}}","kind":"text",
           "subtype":null,"author_id":"{{viewerId}}","audience":"all","payload":null,
           "body":"the one message in the thread","created_at":"{{LiveCreatedAt}}"}]}
        """;

    // =====================================================================
    // 1. THE FIX — the timestamp reaches the device
    // =====================================================================

    [Fact]
    public async Task Get_Messages_Carries_ChatServices_CreatedAt_Through_To_The_Client()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001861");
        upstream.ListBody = LiveListBody(userId);

        var raw = await GetMessagesRaw(http, token);

        var message = ParseWire(raw)["messages"]!.Should().HaveCount(1)
            .And.Subject.First();

        // The whole defect in one assertion: the field must be THERE.
        message["created_at"].Should().NotBeNull(
            "chat-service stamped created_at on this message; the gateway is a relay "
            + "and may not delete it — its absence is what emptied both threads");

        AssertClientParseable(
            message["created_at"],
            "the mobile decoder (_sentAtOf) needs a String it can DateTime.tryParse");

        // …and it must be the SAME INSTANT chat-service stored, not a re-stamped,
        // timezone-shifted or truncated approximation of it.
        AssertSameInstantAs(LiveCreatedAt, message["created_at"]!);
    }

    [Fact]
    public async Task Append_201_Carries_CreatedAt_So_The_Sender_Has_A_Real_Send_Time()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001862");
        upstream.AppendBody =
            $$"""
            {"message_id":"msg-appended","conversation_id":"{{ConversationId}}","kind":"text",
             "subtype":null,"author_id":"{{userId}}","audience":"all","payload":null,
             "body":"hello","created_at":"{{LiveCreatedAt}}"}
            """;

        var post = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/conversations/{ConversationId}/messages")
        {
            Content = new StringContent(
                """{"kind":"text","audience":"all","body":"hello"}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        post.Headers.Authorization = Bearer(token);
        var resp = await http.SendAsync(post);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = ParseWire(await resp.Content.ReadAsStringAsync());

        body["created_at"].Should().NotBeNull(
            "the append response is the sender's only source of the authoritative "
            + "server send time; without it the sender must invent one from its own clock");
        AssertClientParseable(body["created_at"], "the append 201 is decoded by the same client parser");
        AssertSameInstantAs(LiveCreatedAt, body["created_at"]!);
    }

    // =====================================================================
    // 2. ORDERING — the regression the timestamp-less workaround introduced
    // =====================================================================

    /// <summary>
    /// THE CASE THE PREVIOUS FIX'S SUITE DID NOT HAVE. When rows carry no
    /// timestamp, a client can only anchor order on array POSITION, and any later
    /// merge with counterpart traffic scrambles the thread. The cure is a real
    /// per-row timestamp: each row must keep ITS OWN distinct instant, and the
    /// array order the visibility filter chose must survive the relay.
    /// </summary>
    [Fact]
    public async Task Every_Row_Keeps_Its_Own_Distinct_Timestamp_And_The_Upstream_Order()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001863");

        // Three messages, ascending, interleaved between the two participants —
        // the counterpart traffic whose absence let the ordering defect hide.
        var stamps = new[]
        {
            "2026-07-27T13:06:01.100000Z",
            "2026-07-27T13:06:02.200000Z",
            "2026-07-27T13:06:03.300000Z",
        };
        var rows = stamps.Select((ts, i) =>
            $$"""
            {"message_id":"msg-{{i + 1}}","conversation_id":"{{ConversationId}}","kind":"text",
             "subtype":null,"author_id":"{{(i % 2 == 0 ? userId : "the-counterparty")}}",
             "audience":"all","payload":null,"body":"m{{i + 1}}","created_at":"{{ts}}"}
            """);
        upstream.ListBody =
            $"{{\"conversation_id\":\"{ConversationId}\",\"viewer_id\":\"{userId}\","
            + $"\"messages\":[{string.Join(",", rows)}]}}";

        var messages = ParseWire(await GetMessagesRaw(http, token))["messages"]!;

        messages.Should().HaveCount(3);
        for (var i = 0; i < 3; i++)
        {
            AssertClientParseable(messages[i]!["created_at"], $"row {i} must be datable");
            AssertSameInstantAs(stamps[i], messages[i]!["created_at"]!);
        }

        // Distinct instants — not one shared value, and not N copies of a
        // position-derived synthetic stamp.
        var instants = messages
            .Select(m => DateTimeOffset.Parse(
                m["created_at"]!.Value<string>()!, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind))
            .ToList();
        instants.Should().OnlyHaveUniqueItems(
            "each message has its own send time; collapsing them re-creates the "
            + "ordering ambiguity the timestamp exists to remove");
        instants.Should().BeInAscendingOrder(
            "the gateway must relay the array in the order chat-service's "
            + "VisibilityFilter produced it — it may not sort, reverse or re-bucket");

        // And the ids ride along in the same order (order proven by identity, not
        // only by the values we happened to sort on).
        messages.Select(m => m["message_id"]!.Value<string>())
            .Should().Equal("msg-1", "msg-2", "msg-3");
    }

    // =====================================================================
    // 3. NEGATIVE TESTS — proof the assertion above can actually fail
    // =====================================================================

    /// <summary>
    /// POSITIVE CONTROL FOR THE ASSERTION. Reproduces <c>origin/main</c> exactly:
    /// the SAME live chat-service body marshaled through DTOs that do not declare
    /// the timestamp (<see cref="PreFixMessageListResponse"/> — a byte-for-byte copy
    /// of the pre-fix <see cref="JeebMessageListResponse"/>/<see cref="JeebMessageResponse"/>
    /// property set), re-serialized by the same System.Text.Json stack the gateway
    /// response pipeline uses.
    ///
    /// <para>
    /// If this test ever goes green-by-accident — i.e. <c>created_at</c> shows up in
    /// the pre-fix projection — the instrument is broken, not the product. It exists
    /// so nobody has to trust that "the assertion in test 1 is meaningful": here is
    /// the same input producing the FAILING output.
    /// </para>
    /// </summary>
    [Fact]
    public void Break_The_Mapping_And_The_Field_Disappears_ThisIsTheDefect()
    {
        var liveBody = LiveListBody("viewer-1");

        // Sanity: the input genuinely carries the timestamp. Without this the test
        // could "prove" absence from an input that never had it.
        ParseWire(liveBody)["messages"]![0]!["created_at"]!.Value<string>()
            .Should().Be(LiveCreatedAt, "the replayed upstream body must contain the field");

        // --- pre-fix DTO (no timestamp property) ------------------------------
        var preFix = JsonConvert.DeserializeObject<PreFixMessageListResponse>(liveBody)!;
        var preFixWire = Stj.JsonSerializer.Serialize(preFix);

        ParseWire(preFixWire)["messages"]![0]!["created_at"].Should().BeNull(
            "THIS IS THE BUG: a property the DTO does not declare is dropped at the "
            + "typed deserialize/re-serialize hop, silently and with a 200");
        preFixWire.Should().NotContain("created_at");

        // --- fixed DTO (same input, same serializers) -------------------------
        var fixedDto = JsonConvert.DeserializeObject<JeebMessageListResponse>(liveBody)!;
        var fixedWire = Stj.JsonSerializer.Serialize(fixedDto);

        ParseWire(fixedWire)["messages"]![0]!["created_at"].Should().NotBeNull(
            "the shipped DTO must carry it — same body, same serializers, only the "
            + "contract differs");
        AssertSameInstantAs(LiveCreatedAt, ParseWire(fixedWire)["messages"]![0]!["created_at"]!);
    }

    /// <summary>
    /// The other direction of honesty, case 1 of 2: the upstream body OMITS
    /// <c>created_at</c> entirely, and the gateway relays that absence as
    /// <c>null</c>.
    ///
    /// <para>
    /// NAMED FOR WHAT IT ACTUALLY PINS. This test used to be called
    /// <c>An_Upstream_Row_With_No_Timestamp_Relays_Null_Not_The_0001_Husk</c> and
    /// asserted <c>raw.Should().NotContain("0001-01-01")</c> — but its fixture omits
    /// the field, and an omitted field cannot produce the husk under any DTO. The
    /// husk assertion was therefore vacuously true and claimed a behaviour the
    /// gateway did not have: chat-service declares <c>DateTime CreatedAt</c>
    /// NON-nullable, so a real unset row emits <c>"created_at":"0001-01-01T00:00:00"</c>,
    /// which bound to a non-null <see cref="DateTime.MinValue"/> and was re-emitted
    /// verbatim. That case is now covered — for real — by
    /// <see cref="An_Upstream_Row_Carrying_The_0001_Husk_Is_Normalised_To_Null"/>,
    /// which sends the husk instead of assuming it away.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_Upstream_Row_With_The_created_at_Field_ABSENT_Relays_Null()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001864");
        upstream.ListBody =
            $$"""
            {"conversation_id":"{{ConversationId}}","viewer_id":"{{userId}}","messages":[
              {"message_id":"msg-undated","conversation_id":"{{ConversationId}}","kind":"text",
               "subtype":null,"author_id":"{{userId}}","audience":"all","payload":null,
               "body":"undated"}]}
            """;

        var raw = await GetMessagesRaw(http, token);
        var created = ParseWire(raw)["messages"]![0]!["created_at"];

        created!.Type.Should().Be(JTokenType.Null,
            "an absent upstream timestamp is null on the wire");
    }

    /// <summary>
    /// The other direction of honesty, case 2 of 2 — THE ONE THAT ACTUALLY HAPPENS.
    ///
    /// <para>
    /// chat-service cannot omit <c>created_at</c>: it declares the property
    /// NON-nullable (<c>DateTime CreatedAt</c>) over a persistence base type that
    /// initialises it, so an unset row serialises the <c>default(DateTime)</c> husk
    /// <c>0001-01-01T00:00:00</c>. That text binds happily to a non-null
    /// <see cref="DateTime.MinValue"/> on the gateway's <c>DateTime?</c> and, before
    /// the normalising setter, was re-emitted to the device verbatim — a date that
    /// reads as valid to anything that does not special-case year 1.
    /// </para>
    ///
    /// <para>
    /// The fixture below SENDS the husk rather than omitting the field, which is the
    /// difference between pinning this behaviour and assuming it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task An_Upstream_Row_Carrying_The_0001_Husk_Is_Normalised_To_Null()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001865");
        upstream.ListBody =
            $$"""
            {"conversation_id":"{{ConversationId}}","viewer_id":"{{userId}}","messages":[
              {"message_id":"msg-husk","conversation_id":"{{ConversationId}}","kind":"text",
               "subtype":null,"author_id":"{{userId}}","audience":"all","payload":null,
               "body":"husk","created_at":"0001-01-01T00:00:00"}]}
            """;

        // Sanity: the input genuinely carries the husk. Without this the assertions
        // below could "prove" normalisation from a body that never had one — the
        // exact defect this test replaces.
        ParseWire(upstream.ListBody)["messages"]![0]!["created_at"]!.Value<string>()
            .Should().StartWith("0001-01-01");

        var raw = await GetMessagesRaw(http, token);
        var created = ParseWire(raw)["messages"]![0]!["created_at"];

        created!.Type.Should().Be(JTokenType.Null,
            "the husk means 'no send time'; the gateway must say so in the one way "
            + "every reader understands, instead of relaying a year-1 date");
        raw.Should().NotContain("0001-01-01",
            "the default(DateTime) husk is a serializer artefact, not a send time");
    }

    /// <summary>
    /// DISCRIMINATING CONTROL for the husk normalisation: a REAL timestamp must
    /// survive the same setter untouched. Without this, "normalise the husk" could
    /// be satisfied by a setter that nulled everything, and the suite would still be
    /// green while every message on the device lost its clock.
    /// </summary>
    [Fact]
    public void Husk_Normalisation_Does_Not_Touch_A_Real_Timestamp()
    {
        var husk = JsonConvert.DeserializeObject<JeebMessageResponse>(
            """{"message_id":"m","created_at":"0001-01-01T00:00:00"}""")!;
        var real = JsonConvert.DeserializeObject<JeebMessageResponse>(
            $$"""{"message_id":"m","created_at":"{{LiveCreatedAt}}"}""")!;
        var absent = JsonConvert.DeserializeObject<JeebMessageResponse>(
            """{"message_id":"m"}""")!;

        husk.CreatedAt.Should().BeNull("year 1 is not a send time");
        absent.CreatedAt.Should().BeNull("an omitted field was never a send time");
        real.CreatedAt.Should().NotBeNull(
            "the normalisation is a husk guard, not a delete — if this is null the "
            + "setter is nulling everything and the husk assertions are vacuous");

        // …and the real instant is preserved exactly, through BOTH serializer legs.
        AssertSameInstantAs(
            LiveCreatedAt,
            ParseWire(Stj.JsonSerializer.Serialize(real))["created_at"]!);
    }

    // =====================================================================
    // 4. THE REST OF THE FIELD SET — the omission CLASS, not just one instance
    // =====================================================================

    /// <summary>
    /// The audit that would have caught <c>created_at</c> before a phone did: every
    /// key the LIVE chat-service emits on the message projection and on the list
    /// envelope must appear on the gateway's response. Add a field upstream and this
    /// test tells you the relay needs it too.
    /// </summary>
    [Fact]
    public async Task The_Relay_Drops_No_Field_ChatService_Emits()
    {
        var upstream = new ReplayingConversationUpstream();
        using var factory = MakeFactory(upstream);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613001865");
        upstream.ListBody = LiveListBody(userId);

        var wire = ParseWire(await GetMessagesRaw(http, token));
        var upstreamBody = ParseWire(LiveListBody(userId));

        // envelope
        foreach (var key in upstreamBody.Properties().Select(p => p.Name))
        {
            wire.Property(key).Should().NotBeNull(
                $"chat-service emits envelope field '{key}'; the relay must not delete it");
        }

        // message projection
        var upstreamMessage = (JObject)upstreamBody["messages"]![0]!;
        var wireMessage = (JObject)wire["messages"]![0]!;
        foreach (var key in upstreamMessage.Properties().Select(p => p.Name))
        {
            wireMessage.Property(key).Should().NotBeNull(
                $"chat-service emits message field '{key}'; the relay must not delete it");
        }

        // and the viewer echo is the bearer, not a caller-supplied value
        wire["viewer_id"]!.Value<string>().Should().Be(userId);
        wireMessage["conversation_id"]!.Value<string>().Should().Be(ConversationId);
    }

    // =====================================================================
    // wire reader — the instrument, and why the obvious one lies
    // =====================================================================

    /// <summary>
    /// Parse a response body WITHOUT letting Newtonsoft rewrite it.
    ///
    /// <para>
    /// AN INSTRUMENT THAT LIES. <c>JObject.Parse</c> runs with
    /// <c>DateParseHandling.DateTime</c>, so it silently converts any date-shaped
    /// JSON STRING into a <c>JTokenType.Date</c> holding a CLR
    /// <see cref="DateTime"/>. Asserted through that reader, a perfectly correct
    /// <c>"created_at":"2026-07-27T13:06:01.817954Z"</c> reports its type as
    /// <c>Date</c> — a type JSON does not have — and <c>Value&lt;string&gt;()</c>
    /// returns the CLR <c>ToString()</c> rendering instead of the bytes the server
    /// sent. A suite that trusted it could neither prove the wire type the client
    /// requires (String) nor compare the exact text.
    /// </para>
    ///
    /// <para>
    /// <c>DateParseHandling.None</c> makes the reader report what is actually on the
    /// wire. Pinned by <see cref="The_Obvious_Wire_Reader_Lies_About_The_Type"/>.
    /// </para>
    /// </summary>
    private static JObject ParseWire(string json)
    {
        using var reader = new JsonTextReader(new System.IO.StringReader(json))
        {
            DateParseHandling = DateParseHandling.None,
        };
        return JObject.Load(reader);
    }

    /// <summary>
    /// Guards the guard. If this ever fails, <see cref="ParseWire"/>'s reason for
    /// existing has changed and every type assertion in this file must be re-derived.
    /// </summary>
    [Fact]
    public void The_Obvious_Wire_Reader_Lies_About_The_Type()
    {
        const string body = """{"created_at":"2026-07-27T13:06:01.817954Z"}""";

        JObject.Parse(body)["created_at"]!.Type.Should().Be(JTokenType.Date,
            "this is the trap: the default reader rewrites the JSON string as a CLR "
            + "DateTime, so it cannot tell you the wire type the client depends on");

        ParseWire(body)["created_at"]!.Type.Should().Be(JTokenType.String,
            "the honest reader reports what the bytes actually are");
        ParseWire(body)["created_at"]!.Value<string>().Should().Be("2026-07-27T13:06:01.817954Z",
            "and returns the exact text, character for character");
    }

    // =====================================================================
    // assertions
    // =====================================================================

    /// <summary>
    /// What the mobile decoder actually requires: a JSON STRING that
    /// <c>DateTime.tryParse</c> accepts and whose year is &gt; 1 (the husk guard in
    /// <c>dio_chat_gateway.dart</c> → <c>_sentAtOf</c>). A number, an object or a
    /// husk fails the client even though it "has a created_at".
    /// </summary>
    private static void AssertClientParseable(JToken? token, string because)
    {
        token.Should().NotBeNull(because);
        token!.Type.Should().Be(JTokenType.String,
            "the client requires a String (it rejects any other JSON type) — " + because);

        var text = token.Value<string>()!;
        DateTimeOffset.TryParse(
                text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            .Should().BeTrue($"'{text}' must be ISO-8601 parseable — {because}");
        parsed.Year.Should().BeGreaterThan(1,
            "0001-01-01 is the default(DateTime) husk, which the client treats as ABSENT");
        text.Should().EndWith("Z",
            "chat-service stamps UTC (DateTime.UtcNow) and the relay must keep the UTC "
            + "marker; an offset-less timestamp is read as local time by the client");
    }

    /// <summary>
    /// The relayed value must be the SAME INSTANT the upstream sent — compared as
    /// instants so a lossless textual re-render (e.g. trailing-zero trimming) is not
    /// treated as corruption, while a timezone shift or truncation is.
    /// </summary>
    private static void AssertSameInstantAs(string expectedUpstreamText, JToken actual)
    {
        var expected = DateTimeOffset.Parse(
            expectedUpstreamText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
        var got = DateTimeOffset.Parse(
            actual.Value<string>()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

        got.ToUniversalTime().Should().Be(expected.ToUniversalTime(),
            "the relayed send time must equal what chat-service stored — a shifted or "
            + "truncated timestamp mis-orders the thread as surely as a missing one "
            + $"(upstream '{expectedUpstreamText}', relayed '{actual.Value<string>()}')");
    }

    // =====================================================================
    // pre-fix DTO copies — the positive control's "broken mapping"
    // =====================================================================

    /// <summary>
    /// <c>origin/main</c>'s <see cref="JeebMessageResponse"/>, property-for-property,
    /// with the timestamp (and <c>conversation_id</c>) absent — the shape that
    /// emptied the threads. Kept here, never in <c>src</c>, so the defect is
    /// reproducible without reverting the fix.
    /// </summary>
    private sealed class PreFixMessageResponse
    {
        [JsonProperty("message_id")]
        [Stj.Serialization.JsonPropertyName("message_id")]
        public string MessageId { get; set; } = string.Empty;

        [JsonProperty("kind")]
        [Stj.Serialization.JsonPropertyName("kind")]
        public string? Kind { get; set; }

        [JsonProperty("subtype")]
        [Stj.Serialization.JsonPropertyName("subtype")]
        public string? Subtype { get; set; }

        [JsonProperty("author_id")]
        [Stj.Serialization.JsonPropertyName("author_id")]
        public string? AuthorId { get; set; }

        [JsonProperty("audience")]
        [JsonConverter(typeof(RawJsonElementConverter))]
        [Stj.Serialization.JsonPropertyName("audience")]
        public Stj.JsonElement? Audience { get; set; }

        [JsonProperty("payload")]
        [JsonConverter(typeof(RawJsonElementConverter))]
        [Stj.Serialization.JsonPropertyName("payload")]
        public Stj.JsonElement? Payload { get; set; }

        [JsonProperty("body")]
        [Stj.Serialization.JsonPropertyName("body")]
        public string? Body { get; set; }
    }

    /// <summary><c>origin/main</c>'s <see cref="JeebMessageListResponse"/>.</summary>
    private sealed class PreFixMessageListResponse
    {
        [JsonProperty("messages")]
        [Stj.Serialization.JsonPropertyName("messages")]
        public IList<PreFixMessageResponse> Messages { get; set; }
            = new List<PreFixMessageResponse>();
    }

    // =====================================================================
    // harness
    // =====================================================================

    private const string AppId = "jeeb-test-app";

    private static async Task<string> GetMessagesRaw(HttpClient http, string token)
    {
        var get = new HttpRequestMessage(
            HttpMethod.Get, $"/v1/conversations/{ConversationId}/messages");
        get.Headers.Authorization = Bearer(token);
        var resp = await http.SendAsync(get);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return await resp.Content.ReadAsStringAsync();
    }

    private static System.Net.Http.Headers.AuthenticationHeaderValue Bearer(string token) =>
        new("Bearer", token);

    private static WebApplicationFactory<Program> MakeFactory(IJeebConversationClient fake) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IJeebConversationClient>();
                services.AddSingleton(fake);

                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new NoopOtp());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Chat = true;
                    f.Otp = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
        return (json["accessToken"]!.Value<string>()!, json["user"]!["userId"]!.Value<string>()!);
    }

    /// <summary>
    /// A chat-service stand-in that replays a RESPONSE BODY, as text, through the
    /// live inbound marshaling step. It never constructs a DTO by hand: the DTO can
    /// only ever hold what the declared properties can bind, which is precisely the
    /// behaviour under test.
    /// </summary>
    private sealed class ReplayingConversationUpstream : IJeebConversationClient
    {
        /// <summary>Raw chat-service body for the list read.</summary>
        public string ListBody { get; set; } = """{"messages":[]}""";

        /// <summary>Raw chat-service body for the append.</summary>
        public string AppendBody { get; set; } = """{"message_id":"msg-1"}""";

        public Task<JeebMessageListResponse> ListMessagesForViewerAsync(
            string conversationId, string viewerUserId, CancellationToken ct)
            => Task.FromResult(JsonConvert.DeserializeObject<JeebMessageListResponse>(ListBody)!);

        public Task<JeebMessageListResponse> ListMessagesSinceForViewerAsync(
            string conversationId, string viewerUserId, string cursor, CancellationToken ct)
            => ListMessagesForViewerAsync(conversationId, viewerUserId, ct);

        public Task<JeebMessageResponse> AppendMessageAsync(
            string conversationId, AppendJeebMessageRequest request, CancellationToken ct)
            => Task.FromResult(JsonConvert.DeserializeObject<JeebMessageResponse>(AppendBody)!);

        // --- surface not exercised here -----------------------------------

        public Task<JeebConversationResponse> CreateConversationAsync(
            CreateJeebConversationRequest request, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = ConversationId,
                CorrelationKey = request.RequestId,
                Phase = "broadcasting",
                Participants = new List<JeebConversationParticipant>(),
            });

        public Task<JeebConversationResponse> GetConversationByCorrelationAsync(
            string correlationKey, CancellationToken ct)
            => Task.FromResult(new JeebConversationResponse
            {
                ConversationId = ConversationId,
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

    private sealed class NoopOtp : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
