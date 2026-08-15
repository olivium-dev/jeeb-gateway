using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.Conversations.Client;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Cases;

public sealed class CaseEvidenceCollectorContractTests
{
    [Fact]
    public async Task Geo_History_Client_Records_Delivery_Scoped_Point_With_Generic_Track_Contract()
    {
        var handler = new GeoWriteHandler();
        var client = new GeoHistoryClient(Client(handler, "https://geo/"));

        await client.RecordTrackPointAsync(
            "delivery-1", "courier-1", 52.37, 4.89, 6.5,
            DateTimeOffset.Parse("2026-08-05T11:01:00Z"));

        handler.Method.Should().Be(HttpMethod.Post);
        handler.Path.Should().Be("/v1/geo/ping");
        handler.Authorization.Should().Be("Bearer jeeb-gateway:admin");
        using var body = JsonDocument.Parse(handler.Body!);
        body.RootElement.GetProperty("trackId").GetString().Should().Be("delivery-1");
        body.RootElement.GetProperty("actorId").GetString().Should().Be("courier-1");
        body.RootElement.GetProperty("lat").GetDouble().Should().Be(52.37);
        body.RootElement.GetProperty("recordedAt").GetDateTimeOffset()
            .Should().Be(DateTimeOffset.Parse("2026-08-05T11:01:00Z"));
    }

    [Fact]
    public void Geo_History_Client_Is_Resilience_Only_Without_Forwarded_Or_Service_Auth()
    {
        using var factory = new WebApplicationFactory<Program>();
        var handlers = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();
        using var root = handlers.CreateHandler(nameof(IGeoHistoryClient));

        var chain = HandlerTypes(root);
        chain.Should().Contain(name => name.Contains("Resilience", StringComparison.Ordinal));
        chain.Should().NotContain(name => name.Contains("BearerForwarding", StringComparison.Ordinal));
        chain.Should().NotContain(name => name.Contains("ServiceAuthSigning", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Evidence_Uses_Canonical_Paged_Viewer_Chat_Delivery_History_And_Geo_Routes()
    {
        var chat = new ChatHandler();
        var delivery = new DeliveryHandler();
        var geo = new GeoHandler();
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00Z"));
        var collector = new CaseEvidenceCollector(
            new JeebConversationClient(Client(chat, "https://chat/")),
            new CaseDeliveryClient(Client(delivery, "https://delivery/")),
            new GeoHistoryClient(Client(geo, "https://geo/")),
            new StaticOptionsMonitor<CaseEvidenceOptions>(new CaseEvidenceOptions
            {
                SourceTimeout = TimeSpan.FromSeconds(5), MaxChatMessages = 2, MaxGpsPoints = 2,
            }),
            clock,
            NullLogger<CaseEvidenceCollector>.Instance);

        var evidence = await collector.CaptureAsync("delivery-1", "client-1",
            new[] { "dispute_evidence/object-1" }, default);

        evidence.Should().Contain(item => item.Source == "chat_snapshot"
            && item.Status == "complete" && item.Count == 2);
        evidence.Should().Contain(item => item.Source == "delivery_history"
            && item.Status == "complete" && item.Count == 1);
        var gps = evidence.Single(item => item.Source == "gps_pings");
        gps.Status.Should().Be("partial");
        gps.Marker.Should().Be("truncated_max_points");
        gps.Count.Should().Be(2);
        gps.RetentionDays.Should().Be(30, "the gateway must never advertise more than 30 days");
        gps.ExpiresAt.Should().Be(DateTimeOffset.Parse("2026-09-04T12:00:00Z"));
        evidence.Should().Contain(item => item.Source == "cdn_attachments"
            && item.Status == "complete" && item.Count == 1);

        chat.Requests.Should().HaveCount(3);
        chat.Requests[0].PathAndQuery.Should().Be("/api/conversations?correlationKey=delivery-1");
        chat.Requests.Should().OnlyContain(uri => !uri.AbsolutePath.EndsWith("/messages", StringComparison.Ordinal));
        chat.Requests[1].AbsolutePath.Should().Be("/api/conversations/conversation-1/export");
        chat.Requests[1].Query.Should().Contain("viewer=client-1").And.Contain("limit=2");
        chat.Requests[1].Query.Should().NotContain("cursor=").And.NotContain("asOf=");
        Uri.UnescapeDataString(chat.Requests[2].Query).Should().Contain("cursor=chat:opaque+1")
            .And.Contain("asOf=2026-08-05T11:59:00.0000000+00:00")
            .And.Contain("limit=1");

        delivery.Requests.Single().AbsolutePath.Should()
            .Be("/deliveries/delivery-1/status-history");
        geo.Requests.Should().HaveCount(2);
        geo.Requests[0].PathAndQuery.Should().Be("/v1/geo/tracks/delivery-1/history?limit=2");
        Uri.UnescapeDataString(geo.Requests[1].PathAndQuery).Should()
            .Be("/v1/geo/tracks/delivery-1/history?limit=1&cursor=geo:opaque+1");
    }

    [Fact]
    public async Task NonAdvancing_Evidence_Pages_Stop_Without_A_Hot_Loop()
    {
        var chat = new NonAdvancingChatHandler();
        var delivery = new DeliveryHandler();
        var geo = new NonAdvancingGeoHandler();
        var collector = new CaseEvidenceCollector(
            new JeebConversationClient(Client(chat, "https://chat/")),
            new CaseDeliveryClient(Client(delivery, "https://delivery/")),
            new GeoHistoryClient(Client(geo, "https://geo/")),
            new StaticOptionsMonitor<CaseEvidenceOptions>(new CaseEvidenceOptions
            {
                SourceTimeout = TimeSpan.FromSeconds(30),
                MaxChatMessages = 10_000,
                MaxGpsPoints = 10_000,
                MaxPagesPerSource = 20,
            }),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-08-05T12:00:00Z")),
            NullLogger<CaseEvidenceCollector>.Instance);

        var stopwatch = Stopwatch.StartNew();
        var evidence = await collector.CaptureAsync("delivery-1", "client-1", Array.Empty<string>(), default);
        stopwatch.Stop();

        stopwatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
        chat.Requests.Should().HaveCount(2, "conversation lookup plus one non-advancing export must terminate");
        geo.Requests.Should().ContainSingle("one non-advancing geo page must terminate");
        evidence.Should().Contain(item => item.Source == "chat_snapshot"
            && item.Status == "partial" && item.Marker == "non_advancing_page");
        evidence.Should().Contain(item => item.Source == "gps_pings"
            && item.Status == "partial" && item.Marker == "non_advancing_page");
    }

    private static HttpClient Client(HttpMessageHandler handler, string baseAddress) => new(handler)
    {
        BaseAddress = new Uri(baseAddress),
    };

    private static IReadOnlyList<string> HandlerTypes(HttpMessageHandler root)
    {
        var names = new List<string>();
        for (var current = root; current is not null;)
        {
            names.Add(current.GetType().FullName ?? current.GetType().Name);
            current = (current as DelegatingHandler)?.InnerHandler;
        }
        return names;
    }

    private abstract class RecordingHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = new();

        protected sealed override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(Respond(request, Requests.Count));
        }

        protected abstract HttpResponseMessage Respond(HttpRequestMessage request, int call);

        protected static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
    }

    private sealed class ChatHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request, int call) => call switch
        {
            1 => Json("""
                {
                  "conversation_id":"conversation-1",
                  "correlation_key":"delivery-1",
                  "phase":"accepted",
                  "participants":[]
                }
                """),
            2 => Json("""
                {
                  "conversation_id":"conversation-1",
                  "viewer_id":"client-1",
                  "as_of":"2026-08-05T11:59:00Z",
                  "limit":2,
                  "has_more":true,
                  "next_cursor":"chat:opaque+1",
                  "messages":[{"message_id":"message-1","body":"first"}]
                }
                """),
            3 => Json("""
                {
                  "conversation_id":"conversation-1",
                  "viewer_id":"client-1",
                  "as_of":"2026-08-05T11:59:00Z",
                  "limit":1,
                  "has_more":false,
                  "next_cursor":null,
                  "messages":[{"message_id":"message-2","body":"second"}]
                }
                """),
            _ => throw new InvalidOperationException("Unexpected chat request."),
        };
    }

    private sealed class DeliveryHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request, int call) => Json("""
            {
              "delivery_id":"delivery-1",
              "party_ids":{"client_id":"client-1","courier_id":"courier-1"},
              "current_status":"InTransit",
              "status_history":[{
                "transition_id":"transition-1",
                "from_status":"Picked",
                "to_status":"InTransit",
                "trigger":"courier_departed",
                "source":"jeeber",
                "actor_id":"courier-1",
                "transitioned_at":"2026-08-05T11:00:00Z"
              }]
            }
            """);
    }

    private sealed class NonAdvancingChatHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request, int call) => call switch
        {
            1 => Json("""
                {
                  "conversation_id":"conversation-1",
                  "correlation_key":"delivery-1",
                  "phase":"accepted",
                  "participants":[]
                }
                """),
            _ => Json("""
                {
                  "conversation_id":"conversation-1",
                  "viewer_id":"client-1",
                  "as_of":"2026-08-05T11:59:00Z",
                  "limit":500,
                  "has_more":true,
                  "next_cursor":"chat:stuck",
                  "messages":[]
                }
                """),
        };
    }

    private sealed class GeoHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request, int call) => call switch
        {
            1 => Json("""
                {
                  "trackId":"delivery-1",
                  "pings":[{
                    "id":"1a2e908a-e719-4ae3-a51a-e1208d8a82a6",
                    "trackId":"delivery-1",
                    "actorId":"courier-1",
                    "lat":52.37,
                    "lng":4.89,
                    "recordedAt":"2026-08-05T11:01:00Z"
                  }],
                  "nextCursor":"geo:opaque+1",
                  "hasMore":true,
                  "retentionDays":45,
                  "retainedFrom":"2026-07-06T12:00:00Z"
                }
                """),
            2 => Json("""
                {
                  "trackId":"delivery-1",
                  "pings":[{
                    "id":"bf451123-a294-4bb5-86b7-b3edb41e639f",
                    "trackId":"delivery-1",
                    "actorId":"courier-1",
                    "lat":52.38,
                    "lng":4.90,
                    "recordedAt":"2026-08-05T11:02:00Z"
                  }],
                  "nextCursor":"geo:opaque+2",
                  "hasMore":true,
                  "retentionDays":45,
                  "retainedFrom":"2026-07-06T12:00:00Z"
                }
                """),
            _ => throw new InvalidOperationException("Unexpected geo request."),
        };
    }

    private sealed class NonAdvancingGeoHandler : RecordingHandler
    {
        protected override HttpResponseMessage Respond(HttpRequestMessage request, int call) => Json("""
            {
              "trackId":"delivery-1",
              "pings":[],
              "nextCursor":"geo:stuck",
              "hasMore":true,
              "retentionDays":30,
              "retainedFrom":"2026-07-06T12:00:00Z"
            }
            """);
    }

    private sealed class GeoWriteHandler : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Method = request.Method;
            Path = request.RequestUri?.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent(
                    "{\"ping\":{\"id\":\"1a2e908a-e719-4ae3-a51a-e1208d8a82a6\"},\"subscribers\":0}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
