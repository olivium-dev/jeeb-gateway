using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

public interface IGeoHistoryClient
{
    Task<GpsTrackHistoryPage> GetTrackHistoryPageAsync(
        string trackId,
        string? cursor,
        int limit = 500,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Private-network geo evidence reader. The gateway has already authorized the
/// caller, so this client deliberately carries resilience only and no auth headers.
/// </summary>
public sealed class GeoHistoryClient(HttpClient httpClient) : IGeoHistoryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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
        using var response = await httpClient.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new GpsTrackHistoryPage { Available = false };

        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<GpsTrackHistoryPage>(
            JsonOptions, cancellationToken).ConfigureAwait(false);
        return (page ?? new GpsTrackHistoryPage()) with { Available = true };
    }
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
