using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

public interface IGeoHistoryClient
{
    Task RecordTrackPointAsync(
        string trackId,
        string actorId,
        double lat,
        double lng,
        double? accuracyM,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default);

    Task<GpsTrackHistoryPage> GetTrackHistoryPageAsync(
        string trackId,
        string? cursor,
        int limit = 500,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Generic private-network geo track client. History reads need no auth header;
/// writes use geolocation-service's existing opaque internal identity contract.
/// </summary>
public sealed class GeoHistoryClient : IGeoHistoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly GeoHistoryWriteOptions _writeOptions;
    private readonly ILogger<GeoHistoryClient> _logger;

    [ActivatorUtilitiesConstructor]
    public GeoHistoryClient(
        HttpClient httpClient,
        IOptions<GeoHistoryWriteOptions> writeOptions,
        ILogger<GeoHistoryClient> logger)
    {
        _httpClient = httpClient;
        _writeOptions = writeOptions.Value;
        _logger = logger;
    }

    // Keeps direct contract-test construction terse. Production DI supplies the
    // configured bounded throttle policy and logger.
    public GeoHistoryClient(HttpClient httpClient)
        : this(
            httpClient,
            Options.Create(new GeoHistoryWriteOptions()),
            NullLogger<GeoHistoryClient>.Instance)
    {
    }

    public async Task RecordTrackPointAsync(
        string trackId,
        string actorId,
        double lat,
        double lng,
        double? accuracyM,
        DateTimeOffset recordedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            throw new ArgumentException("trackId is required.", nameof(trackId));
        if (string.IsNullOrWhiteSpace(actorId))
            throw new ArgumentException("actorId is required.", nameof(actorId));

        var point = new GeoTrackPointWrite
        {
            TrackId = trackId,
            ActorId = actorId,
            Lat = lat,
            Lng = lng,
            AccuracyM = accuracyM,
            RecordedAt = recordedAt,
        };

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await SendTrackPointOnceAsync(point, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (GeoHistoryThrottledException error)
                when (attempt < Math.Max(0, _writeOptions.MaxThrottleRetries))
            {
                var requested = error.RetryAfter
                    ?? TimeSpan.FromMilliseconds(Math.Max(1, _writeOptions.ThrottleFallbackDelayMs));
                var delay = TimeSpan.FromMilliseconds(Math.Clamp(
                    requested.TotalMilliseconds,
                    1,
                    Math.Max(1, _writeOptions.MaxThrottleDelayMs)));

                _logger.LogInformation(
                    "Geo-history write throttled for track {TrackId}; retry {Retry}/{MaxRetries} in {DelayMs}ms.",
                    trackId, attempt + 1, _writeOptions.MaxThrottleRetries, delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task SendTrackPointOnceAsync(
        GeoTrackPointWrite point,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/geo/ping")
        {
            Content = JsonContent.Create(point, options: JsonOptions),
        };

        // geolocation-service currently exposes its generic private-network ingest
        // with the documented opaque AuthContext shape. Admin is the integration
        // role and the actor remains explicit in the product-neutral payload.
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", "jeeb-gateway:admin");
        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new GeoHistoryThrottledException(ReadRetryAfter(response));

        response.EnsureSuccessStatusCode();
    }

    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta;
        if (retryAfter?.Date is { } date)
            return date - DateTimeOffset.UtcNow;
        return null;
    }

    public async Task<GpsTrackHistoryPage> GetTrackHistoryPageAsync(
        string trackId,
        string? cursor,
        int limit = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(trackId))
            throw new ArgumentException("trackId is required.", nameof(trackId));

        var path = "v1/geo/tracks/" + Uri.EscapeDataString(trackId)
            + "/history?limit=" + Math.Clamp(limit, 1, 500).ToString(CultureInfo.InvariantCulture)
            + (string.IsNullOrWhiteSpace(cursor)
                ? string.Empty
                : "&cursor=" + Uri.EscapeDataString(cursor));
        using var response = await _httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new GpsTrackHistoryPage { Available = false };

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<GpsTrackHistoryPage>(
            JsonOptions, cancellationToken).ConfigureAwait(false);
        return (page ?? new GpsTrackHistoryPage()) with { Available = true };
    }
}

public sealed class GeoHistoryWriteOptions
{
    public const string SectionName = "Tracking:GeoHistoryWrite";

    public int MaxThrottleRetries { get; set; } = 4;
    public int ThrottleFallbackDelayMs { get; set; } = 1000;
    public int MaxThrottleDelayMs { get; set; } = 6000;
}

internal sealed class GeoHistoryThrottledException(TimeSpan? retryAfter)
    : HttpRequestException("Geolocation track history write was throttled.", null, HttpStatusCode.TooManyRequests)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

internal sealed class GeoTrackPointWrite
{
    [JsonPropertyName("trackId")]
    public required string TrackId { get; init; }

    [JsonPropertyName("actorId")]
    public required string ActorId { get; init; }

    [JsonPropertyName("lat")]
    public double Lat { get; init; }

    [JsonPropertyName("lng")]
    public double Lng { get; init; }

    [JsonPropertyName("accuracyM")]
    public double? AccuracyM { get; init; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; init; }
}

public sealed record GpsTrackHistoryPage
{
    [JsonIgnore]
    public bool Available { get; init; }

    [JsonPropertyName("trackId")]
    public string? TrackId { get; init; }

    [JsonPropertyName("pings")]
    public IReadOnlyList<GpsTrackHistoryPoint> Pings { get; init; }
        = Array.Empty<GpsTrackHistoryPoint>();

    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; init; }

    [JsonPropertyName("retainedFrom")]
    public DateTimeOffset RetainedFrom { get; init; }
}

public sealed class GpsTrackHistoryPoint
{
    [JsonPropertyName("lat")]
    public double Lat { get; init; }

    [JsonPropertyName("lng")]
    public double Lng { get; init; }

    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("trackId")]
    public string? TrackId { get; init; }

    [JsonPropertyName("actorId")]
    public string? ActorId { get; init; }

    [JsonPropertyName("accuracyM")]
    public double? AccuracyM { get; init; }

    [JsonPropertyName("headingDeg")]
    public double? HeadingDeg { get; init; }

    [JsonPropertyName("speedMps")]
    public double? SpeedMps { get; init; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; init; }

    [JsonPropertyName("receivedAt")]
    public DateTimeOffset? ReceivedAt { get; init; }
}
