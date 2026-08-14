using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Contract-seam guard for the realtime-comunication-service wire
/// (FeatureFlags:UseUpstream:Realtime). The upstream (Elixir/Phoenix "LiveComm")
/// exposes NO OpenAPI document, so <see cref="RealtimeCommunicationClient"/> is
/// hand-coded against the HTTP ingest route verified in
/// <c>realtime-comunication-service/lib/live_comm_web/router.ex</c> +
/// <c>controllers/ingest_controller.ex</c>:
/// <c>POST /api/ingest/{topic}/{stream}</c> with body
/// <c>{ "data": {...}, "meta": {...} }</c> → <c>202 { ok, id, seq }</c>, and
/// explicit <c>401 / 403 / 429</c> error envelopes.
///
/// The rest of the suite never reaches this upstream (the realtime flag is off and
/// the service is not deployed), so the REAL JSON seam + the per-recipient stream
/// encoding + the status→exception mapping are exercised only here. Fake-handler
/// tests always run and are CI-authoritative; a live-wire test is opt-in via
/// <c>JEEB_REALTIME_LIVE=1</c> (skipped by default — CI has no route to the
/// upstream's private network, and the service is not yet deployed at all).
/// </summary>
public class RealtimeCommunicationClientContractTests
{
    private const string RecipientId = "11111111-1111-1111-1111-111111111111";
    private const string MessageId = "msg-1234";

    // -----------------------------------------------------------------------
    // Fake-handler seam tests (always run)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task FanOut_Posts_To_Ingest_With_Escaped_Topic_And_PerRecipient_Stream()
    {
        HttpRequestMessage? captured = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted,
            """{"ok":true,"id":"env-1","seq":7}""",
            (req, _) => captured = req);

        var data = new Dictionary<string, object?> { ["messageId"] = MessageId, ["type"] = "text" };
        await client.FanOutChatMessageAsync(RecipientId, data, CancellationToken.None);

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        // jeeb:chat → jeeb%3Achat ; user:{id} → user%3A{id}. The per-recipient
        // fan-out filter: one recipient per publish, encoded into the stream.
        captured.RequestUri!.AbsolutePath
            .Should().Be($"/api/ingest/jeeb%3Achat/user%3A{RecipientId}");
    }

    [Fact]
    public async Task FanOut_Sends_Data_Envelope_And_Binds_202_Result()
    {
        string? body = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted,
            """{"ok":true,"id":"env-9","seq":42}""",
            (_, b) => body = b);

        var data = new Dictionary<string, object?>
        {
            ["messageId"] = MessageId,
            ["senderId"] = "sender-1",
            ["type"] = "text",
            ["body"] = "hi",
        };

        var result = await client.FanOutChatMessageAsync(RecipientId, data, CancellationToken.None);

        // Body carries the ingest { data: {...} } envelope (Web JSON → camelCase).
        body.Should().Contain("\"data\"").And.Contain("\"messageId\":\"msg-1234\"");
        result.Ok.Should().BeTrue();
        result.Id.Should().Be("env-9");
        result.Seq.Should().Be(42);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Publish_Maps_NonSuccess_To_RealtimePublishException_With_Status(
        HttpStatusCode status)
    {
        var client = ClientReturning(status, """{"error":"x"}""");

        var act = async () => await client.FanOutChatMessageAsync(
            RecipientId,
            new Dictionary<string, object?> { ["type"] = "text" },
            CancellationToken.None);

        (await act.Should().ThrowAsync<RealtimePublishException>())
            .Which.StatusCode.Should().Be(status);
    }

    [Fact]
    public async Task Publish_429_Carries_Json_Throttle_Delay_For_Bounded_Background_Retry()
    {
        var client = ClientReturning(
            HttpStatusCode.TooManyRequests,
            "{\"error\":\"throttled\",\"next_allowed_ms\":850}",
            retryAfter: TimeSpan.Zero);

        var action = () => client.PublishAsync(
            "jeeb:delivery:d-1",
            "location",
            new Dictionary<string, object?> { ["jeeberId"] = "courier-1" },
            null,
            CancellationToken.None);

        var error = (await action.Should().ThrowAsync<RealtimePublishException>()).Which;
        error.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        error.RetryAfter.Should().Be(TimeSpan.FromMilliseconds(850),
            "the precise JSON hint must win over the service's truncated Retry-After header");
    }

    [Fact]
    public async Task Publish_Rejects_Blank_Recipient()
    {
        var client = ClientReturning(HttpStatusCode.Accepted, """{"ok":true,"id":"x","seq":1}""");

        var act = async () => await client.FanOutChatMessageAsync(
            "  ", new Dictionary<string, object?>(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Chat_Topic_Defaults_To_The_Live_Literal_And_Follows_The_Configured_Prefix()
    {
        // Lock the default so behavior is byte-identical until the config flips.
        Names().ChatTopic.Should().Be("jeeb:chat");
        Names("acme").ChatTopic.Should().Be("acme:chat");
    }

    [Fact]
    public async Task FanOut_Ingest_Path_Follows_The_Configured_Tenant_Prefix()
    {
        HttpRequestMessage? captured = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted,
            """{"ok":true,"id":"env-1","seq":7}""",
            (req, _) => captured = req,
            topics: Names("acme"));

        await client.FanOutChatMessageAsync(
            RecipientId,
            new Dictionary<string, object?> { ["type"] = "text" },
            CancellationToken.None);

        captured!.RequestUri!.AbsolutePath
            .Should().Be($"/api/ingest/acme%3Achat/user%3A{RecipientId}");
    }

    // -----------------------------------------------------------------------
    // Live-wire test (opt-in; skipped by default — service not yet deployed)
    // -----------------------------------------------------------------------

    [Fact]
    public async Task LiveWire_Ingest_Without_Bearer_Is_Unauthorized()
    {
        if (!LiveEnabled(out var baseUrl)) return;

        using var http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/") };
        using var resp = await http.PostAsync(
            $"api/ingest/jeeb%3Achat/user%3A{RecipientId}",
            new StringContent("""{"data":{"type":"text"}}""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static bool LiveEnabled(out string baseUrl)
    {
        baseUrl = Environment.GetEnvironmentVariable("JEEB_REALTIME_BASEURL")
                  ?? "http://127.0.0.1:4000";
        return Environment.GetEnvironmentVariable("JEEB_REALTIME_LIVE") == "1";
    }

    // -----------------------------------------------------------------------
    // Fake handler plumbing
    // -----------------------------------------------------------------------

    private static RealtimeCommunicationClient ClientReturning(
        HttpStatusCode status,
        string json,
        TimeSpan? retryAfter = null)
        => new(
            new HttpClient(new StubHandler(status, json, retryAfter: retryAfter))
            {
                BaseAddress = new Uri("http://realtime-service.test/")
            },
            Issuer(secret: null),
            Names());

    private static RealtimeCommunicationClient ClientCapturing(
        HttpStatusCode status, string json, Action<HttpRequestMessage, string?> capture,
        string? guardianSecret = null,
        JeebGateway.Realtime.RealtimeTopicNames? topics = null)
        => new(
            new HttpClient(new StubHandler(status, json, capture))
            {
                BaseAddress = new Uri("http://realtime-service.test/")
            },
            Issuer(guardianSecret),
            topics ?? Names());

    /// <summary>Topic names for a prefix; the default pins today's live literals.</summary>
    private static JeebGateway.Realtime.RealtimeTopicNames Names(string prefix = "jeeb")
        => new(Microsoft.Extensions.Options.Options.Create(
            new JeebGateway.Realtime.RealtimeGuardianOptions { TenantPrefix = prefix }));

    /// <summary>
    /// A <c>null</c> secret yields an issuer that mints nothing, so these wire-shape tests
    /// see exactly the request they saw before the realtime credential existed.
    /// </summary>
    private static JeebGateway.Realtime.IRealtimeGuardianTokenIssuer Issuer(string? secret)
        => new JeebGateway.Realtime.RealtimeGuardianTokenIssuer(
            Microsoft.Extensions.Options.Options.Create(
                new JeebGateway.Realtime.RealtimeGuardianOptions { GuardianSecret = secret }),
            TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                JeebGateway.Realtime.RealtimeGuardianTokenIssuer>.Instance);

    /// <summary>
    /// The upstream authenticates every ingest against its OWN Guardian secret, so a
    /// publish that carries no credential (or carries the forwarded gateway bearer) is
    /// rejected 401. Pin that the client attaches a topic-scoped credential when one can
    /// be minted — and that it is scoped to the topic being published, not to "*".
    /// </summary>
    [Fact]
    public async Task PublishAsync_Attaches_A_Topic_Scoped_Guardian_Credential()
    {
        HttpRequestMessage? seen = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted, "{\"ok\":true,\"id\":\"x\",\"seq\":1}",
            (req, _) => seen = req,
            guardianSecret: "contract-test-guardian-secret-0123456789-0123456789-abcdef");

        await client.PublishAsync(
            "jeeb:delivery:d-1", "location",
            new Dictionary<string, object?> { ["lat"] = 1.0 }, null, default);

        seen!.Headers.Authorization.Should().NotBeNull();
        seen.Headers.Authorization!.Scheme.Should().Be("Bearer");

        var payload = seen.Headers.Authorization.Parameter!.Split('.')[1];
        payload = payload.Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;

        claims.GetProperty("topics").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("jeeb:delivery:d-1");
        claims.GetProperty("scopes").EnumerateArray().Select(e => e.GetString())
            .Should().Equal("publish");
    }

    [Fact]
    public async Task Location_Publish_Uses_PerCourier_Guardian_Subject_For_Rate_Limit_Isolation()
    {
        HttpRequestMessage? seen = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted, "{\"ok\":true,\"id\":\"x\",\"seq\":1}",
            (req, _) => seen = req,
            guardianSecret: "contract-test-guardian-secret-0123456789-0123456789-abcdef");

        await client.PublishAsync(
            "jeeb:delivery:d-1", "location",
            new Dictionary<string, object?>
            {
                ["lat"] = 1.0,
                ["lng"] = 2.0,
                ["jeeberId"] = "courier-1",
            }, null, default);

        var payload = seen!.Headers.Authorization!.Parameter!.Split('.')[1]
            .Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = System.Text.Json.JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
        claims.GetProperty("sub").GetString().Should().Be("jeeb-gateway:location:courier-1");
    }

    /// <summary>
    /// NEGATIVE CONTROL for the test above: with no secret configured nothing is minted,
    /// so the assertion above is detecting a real attachment rather than always passing.
    /// </summary>
    [Fact]
    public async Task PublishAsync_Attaches_No_Credential_When_No_Secret_Is_Configured()
    {
        HttpRequestMessage? seen = null;
        var client = ClientCapturing(
            HttpStatusCode.Accepted, "{\"ok\":true,\"id\":\"x\",\"seq\":1}", (req, _) => seen = req);

        await client.PublishAsync(
            "jeeb:delivery:d-1", "location",
            new Dictionary<string, object?> { ["lat"] = 1.0 }, null, default);

        seen!.Headers.Authorization.Should().BeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        private readonly Action<HttpRequestMessage, string?>? _capture;
        private readonly TimeSpan? _retryAfter;

        public StubHandler(HttpStatusCode status, string json,
            Action<HttpRequestMessage, string?>? capture = null,
            TimeSpan? retryAfter = null)
        {
            _status = status;
            _json = json;
            _capture = capture;
            _retryAfter = retryAfter;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _capture?.Invoke(request, body);

            var response = new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            if (_retryAfter is not null)
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
                    _retryAfter.Value);
            return response;
        }
    }
}
