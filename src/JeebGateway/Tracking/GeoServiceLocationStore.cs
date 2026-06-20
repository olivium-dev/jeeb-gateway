using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Generated = JeebGateway.Services.Generated.GeolocationService;
using GeneratedClient = JeebGateway.Services.Generated.GeolocationService.IGeolocationServiceClient;

namespace JeebGateway.Tracking;

/// <summary>
/// Upstream-backed <see cref="ILocationStore"/> that delegates to the shared,
/// product-agnostic geolocation-service (Python / FastAPI) via the NSwag-shaped
/// <see cref="GeneratedClient"/>. Selected over
/// <see cref="InMemoryLocationStore"/> at DI time when
/// <c>FeatureFlags:UseUpstream:Geolocation</c> is true — the
/// <see cref="BanServiceJeeberRestrictionStore"/> precedent: a flag-gated swap of
/// the record-of-truth behind the <see cref="ILocationStore"/> seam so neither the
/// controller nor the SSE loop branch on the flag.
///
/// <para><b>Semantic mapping — read first.</b> The gateway's store contract is a
/// per-Jeeber "latest non-expired fix with a TTL" (see <see cref="ILocationStore"/>).
/// geolocation-service is generic and owns persistence, so this wrapper:</para>
/// <list type="bullet">
///   <item><b>Record</b> (write): posts the batch to <c>POST /location/update</c>
///   (snake_case <c>points[].lat/lng/accuracy/timestamp</c>) and maps the upstream
///   <c>LocationUpdateResponse</c> (accepted / rejected / latest) back onto
///   <see cref="LocationStoreUpdateResult"/>. The upstream <c>latest</c> fix becomes
///   the result's <see cref="StoredPosition"/>.</item>
///   <item><b>GetLatest</b> (read): reads <c>GET /locations/user/{id}</c>. A 404 maps
///   to <c>null</c> (no fix on record), NOT an exception. A fix whose upstream
///   <c>created_at</c> is older than <see cref="TrackingOptions.PositionTtl"/> maps to
///   <c>null</c> too, mirroring the in-memory store's lazy-TTL contract so callers
///   see identical "stale == no current fix" behaviour regardless of the flag.</item>
/// </list>
///
/// <para><b>Sync-over-async.</b> <see cref="ILocationStore"/> is synchronous (the hot
/// read path was designed lock-free for the 50k-updates/min budget). The generated
/// client is async, so the two delegating methods bridge with
/// <c>GetAwaiter().GetResult()</c>. This is acceptable only because the typed
/// HttpClient is configured with a bounded timeout + resilience pipeline in
/// <c>ServiceClientExtensions</c>; a future change to an async ILocationStore would
/// remove the bridge. The bridge is isolated to this flag-OFF-by-default path.</para>
/// </summary>
public sealed class GeoServiceLocationStore : ILocationStore
{
    private readonly GeneratedClient _client;
    private readonly IOptionsMonitor<TrackingOptions> _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<GeoServiceLocationStore> _logger;

    public GeoServiceLocationStore(
        GeneratedClient client,
        IOptionsMonitor<TrackingOptions> options,
        TimeProvider clock,
        ILogger<GeoServiceLocationStore> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public LocationStoreUpdateResult Record(string jeeberId, IReadOnlyList<GpsPointDto> points)
    {
        if (string.IsNullOrEmpty(jeeberId)) throw new ArgumentException("jeeberId required", nameof(jeeberId));
        if (points is null || points.Count == 0)
        {
            return new LocationStoreUpdateResult(0, 0, GetLatest(jeeberId));
        }

        var body = new Generated.LocationUpdateRequest
        {
            Points = points
                .Select(p => new Generated.GpsBatchPoint
                {
                    Lat = p.Lat,
                    Lng = p.Lng,
                    Accuracy = p.Accuracy,
                    Timestamp = p.Timestamp,
                })
                .ToList(),
        };

        // sync-over-async bridge — see class banner.
        var response = _client.UpdateLocationAsync(body).GetAwaiter().GetResult();

        var latest = MapLatest(response.Latest);
        return new LocationStoreUpdateResult(response.Accepted, response.Rejected, latest);
    }

    public StoredPosition? GetLatest(string jeeberId)
    {
        if (string.IsNullOrEmpty(jeeberId)) return null;

        // sync-over-async bridge — see class banner.
        var upstream = _client.GetUserLocationAsync(jeeberId).GetAwaiter().GetResult();
        if (upstream is null)
        {
            // 404 from /locations/user/{id} == no fix, not an error.
            return null;
        }

        var receivedAt = upstream.CreatedAt ?? _clock.GetUtcNow();
        if (_clock.GetUtcNow() - receivedAt > _options.CurrentValue.PositionTtl)
        {
            // Stale upstream fix maps to "no current fix", matching the in-memory
            // store's lazy-TTL contract so the flag is behaviourally invisible.
            return null;
        }

        return new StoredPosition(
            upstream.Latitude,
            upstream.Longitude,
            Accuracy: null,
            DeviceTimestamp: receivedAt,
            ReceivedAt: receivedAt);
    }

    private StoredPosition? MapLatest(Generated.GpsBatchLatest? latest)
    {
        if (latest is null) return null;

        var deviceTimestamp = latest.Timestamp ?? _clock.GetUtcNow();
        return new StoredPosition(
            latest.Lat,
            latest.Lng,
            latest.Accuracy,
            DeviceTimestamp: deviceTimestamp,
            ReceivedAt: _clock.GetUtcNow());
    }
}
