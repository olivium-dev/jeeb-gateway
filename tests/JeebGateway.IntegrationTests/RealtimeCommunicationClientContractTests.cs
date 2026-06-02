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
    public async Task Publish_Rejects_Blank_Recipient()
    {
        var client = ClientReturning(HttpStatusCode.Accepted, """{"ok":true,"id":"x","seq":1}""");

        var act = async () => await client.FanOutChatMessageAsync(
            "  ", new Dictionary<string, object?>(), CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public void Publish_Uses_Fixed_Jeeb_Chat_Topic_Constant()
    {
        // Lock the product topic so a rename is a deliberate, test-breaking change.
        RealtimeCommunicationClient.ChatTopic.Should().Be("jeeb:chat");
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
                  ?? "http://192.168.2.50:4000";
        return Environment.GetEnvironmentVariable("JEEB_REALTIME_LIVE") == "1";
    }

    // -----------------------------------------------------------------------
    // Fake handler plumbing
    // -----------------------------------------------------------------------

    private static RealtimeCommunicationClient ClientReturning(HttpStatusCode status, string json)
        => new(new HttpClient(new StubHandler(status, json))
        {
            BaseAddress = new Uri("http://realtime-service.test/")
        });

    private static RealtimeCommunicationClient ClientCapturing(
        HttpStatusCode status, string json, Action<HttpRequestMessage, string?> capture)
        => new(new HttpClient(new StubHandler(status, json, capture))
        {
            BaseAddress = new Uri("http://realtime-service.test/")
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;
        private readonly Action<HttpRequestMessage, string?>? _capture;

        public StubHandler(HttpStatusCode status, string json,
            Action<HttpRequestMessage, string?>? capture = null)
        {
            _status = status;
            _json = json;
            _capture = capture;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            _capture?.Invoke(request, body);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
        }
    }
}
