using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Realtime;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IRealtimeCommunicationClient"/>.
/// Targets realtime-comunication-service's HTTP ingest seam
/// (<c>POST /api/ingest/{topic}/{stream}</c>). The named "realtime" HttpClient
/// (registered in <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>)
/// supplies BaseAddress + the org-standard bearer / X-Service-Auth / resilience
/// chain, so this class never has to think about retry/timeout/circuit-breaker.
///
/// The Phoenix controller emits camelCase-free snake_case-free atom-keyed JSON
/// (<c>ok</c>, <c>id</c>, <c>seq</c>) and reads a body of
/// <c>{ "data": {...}, "meta": {...} }</c>, so the default
/// <see cref="JsonSerializerDefaults.Web"/> options bind it without per-field
/// attributes. The topic/stream path segments are URL-escaped because the Jeeb
/// product topic (<c>jeeb:chat</c>) and stream (<c>user:{id}</c>) both contain a
/// colon.
/// </summary>
public sealed class RealtimeCommunicationClient : IRealtimeCommunicationClient
{
    /// <summary>The fixed product topic for Jeeb 1:1 chat fan-out.</summary>
    public const string ChatTopic = "jeeb:chat";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly IRealtimeGuardianTokenIssuer _guardian;

    public RealtimeCommunicationClient(HttpClient http, IRealtimeGuardianTokenIssuer guardian)
    {
        _http = http;
        _guardian = guardian;
    }

    public async Task<RealtimePublishResult> PublishAsync(
        string topic,
        string stream,
        IReadOnlyDictionary<string, object?> data,
        IReadOnlyDictionary<string, object?>? meta,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            throw new ArgumentException("topic is required.", nameof(topic));
        }
        if (string.IsNullOrWhiteSpace(stream))
        {
            throw new ArgumentException("stream is required.", nameof(stream));
        }
        ArgumentNullException.ThrowIfNull(data);

        // POST /api/ingest/{topic}/{stream} — IngestController.publish/2.
        // Both segments are escaped: jeeb:chat / user:{id} contain a colon.
        var url = $"api/ingest/{Uri.EscapeDataString(topic)}/{Uri.EscapeDataString(stream)}";

        var body = new IngestBody
        {
            Data = data,
            Meta = meta,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };

        // The upstream authenticates ingest with ITS OWN Guardian secret
        // (IngestController.authenticate/1), which the gateway's forwarded user bearer
        // cannot satisfy. Mint a credential narrowed to exactly this topic with publish
        // scope only, so a leaked publish credential grants one topic and no reads.
        // BearerForwardingHandler leaves an already-set Authorization header alone, so
        // this wins over the inbound bearer; when no secret is configured we set nothing
        // and behaviour is exactly what it was before this path existed.
        var credential = _guardian.Issue(
            subject: PublishSubject(stream, data),
            topic: topic,
            scopes: RealtimeGuardianTokenIssuer.PublishOnly);
        if (credential is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Token);
        }

        using var response = await _http.SendAsync(request, ct);

        // The upstream returns explicit 401/403/429 envelopes; surface them as a
        // typed HttpRequestException carrying the status so the controller can map
        // them to RFC 7807 without re-reading the body.
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = await ReadRetryAfterAsync(response, ct);
            throw new RealtimePublishException(
                response.StatusCode,
                $"realtime-comunication-service ingest {topic}/{stream} returned {(int)response.StatusCode}.",
                retryAfter);
        }

        var payload = await response.Content.ReadFromJsonAsync<IngestResultWire>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new RealtimePublishResult
        {
            Ok = payload.Ok,
            Id = payload.Id,
            Seq = payload.Seq,
        };
    }

    public Task<RealtimePublishResult> FanOutChatMessageAsync(
        string recipientId,
        IReadOnlyDictionary<string, object?> data,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            throw new ArgumentException("recipientId is required.", nameof(recipientId));
        }

        // Per-recipient fan-out filter: one recipient per publish, encoded into the
        // stream so only that user's subscription receives the 1:1 message.
        var stream = $"user:{recipientId}";
        return PublishAsync(ChatTopic, stream, data, meta: null, ct);
    }

    private static string PublishSubject(
        string stream,
        IReadOnlyDictionary<string, object?> data)
    {
        // HTTP ingest rate limiting is keyed by Guardian subject. A single fixed
        // gateway subject makes every courier share one 100/minute bucket, so GPS
        // publication is partitioned by the already-authorized courier identity.
        if (string.Equals(stream, CourierPositionTopic.Stream, StringComparison.Ordinal)
            && data.TryGetValue("jeeberId", out var value)
            && value is not null
            && !string.IsNullOrWhiteSpace(value.ToString()))
        {
            return "jeeb-gateway:location:" + value;
        }

        return "jeeb-gateway";
    }

    private static async Task<TimeSpan?> ReadRetryAfterAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            using var document = JsonDocument.Parse(body);
            foreach (var property in new[] { "next_allowed_ms", "retry_after_ms", "retryAfterMs" })
            {
                if (document.RootElement.TryGetProperty(property, out var value)
                    && value.TryGetInt32(out var milliseconds))
                    return TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
            }
        }
        catch (JsonException)
        {
            // A malformed error envelope still maps to the original status.
        }

        // The deployed service emits an exact millisecond JSON hint and an RFC header
        // truncated to whole seconds. Prefer JSON so sub-second backoff is not lost.
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }

    // --- wire DTOs ---

    private sealed class IngestBody
    {
        public required IReadOnlyDictionary<string, object?> Data { get; init; }
        public IReadOnlyDictionary<string, object?>? Meta { get; init; }
    }

    private sealed class IngestResultWire
    {
        public bool Ok { get; init; }
        public string? Id { get; init; }
        public long Seq { get; init; }
    }
}

/// <summary>
/// Raised when realtime-comunication-service rejects an ingest publish with a
/// non-2xx status (401 unauthorized, 403 forbidden ACL, 429 throttled/rate
/// limited). Carries the upstream <see cref="StatusCode"/> so the controller can
/// translate to the matching RFC 7807 ProblemDetails without re-reading the body.
/// </summary>
public sealed class RealtimePublishException : HttpRequestException
{
    public RealtimePublishException(
        HttpStatusCode statusCode,
        string message,
        TimeSpan? retryAfter = null)
        : base(message)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>The upstream HTTP status that triggered the failure.</summary>
    public new HttpStatusCode StatusCode { get; }

    public TimeSpan? RetryAfter { get; }
}
