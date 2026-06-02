using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    public RealtimeCommunicationClient(HttpClient http)
    {
        _http = http;
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

        using var response = await _http.PostAsJsonAsync(url, body, JsonOptions, ct);

        // The upstream returns explicit 401/403/429 envelopes; surface them as a
        // typed HttpRequestException carrying the status so the controller can map
        // them to RFC 7807 without re-reading the body.
        if (!response.IsSuccessStatusCode)
        {
            throw new RealtimePublishException(
                response.StatusCode,
                $"realtime-comunication-service ingest {topic}/{stream} returned {(int)response.StatusCode}.");
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
    public RealtimePublishException(HttpStatusCode statusCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>The upstream HTTP status that triggered the failure.</summary>
    public new HttpStatusCode StatusCode { get; }
}
