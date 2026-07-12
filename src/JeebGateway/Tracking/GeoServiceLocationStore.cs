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
/// <para><b>Fully async (JEBV4-57 / GW12-PERF-1).</b> <see cref="ILocationStore"/>
/// is now async, so both delegating methods <c>await</c> the generated client
/// directly. The previous <c>GetAwaiter().GetResult()</c> sync-over-async bridge is
/// GONE: on the GPS hot path (50k updates/min budget) a blocking bridge would pin an
/// ASP.NET thread-pool thread per in-flight upstream call, so a GPS fan-out storm
/// with <c>UseUpstream:Geolocation</c> flipped on could starve unrelated request
/// handling. Awaiting frees the thread during the network round-trip; the typed
/// HttpClient still carries the bounded timeout + resilience pipeline from
/// <c>ServiceClientExtensions</c>.</para>
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

    public async Task<LocationStoreUpdateResult> RecordAsync(string jeeberId, IReadOnlyList<GpsPointDto> points, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jeeberId)) throw new ArgumentException("jeeberId required", nameof(jeeberId));
        if (points is null || points.Count == 0)
        {
            return new LocationStoreUpdateResult(0, 0, await GetLatestAsync(jeeberId, ct).ConfigureAwait(false));
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

        var response = await _client.UpdateLocationAsync(body, ct).ConfigureAwait(false);

        var latest = MapLatest(response.Latest);
        return new LocationStoreUpdateResult(response.Accepted, response.Rejected, latest);
    }

    public async Task<StoredPosition?> GetLatestAsync(string jeeberId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jeeberId)) return null;

        var upstream = await _client.GetUserLocationAsync(jeeberId, ct).ConfigureAwait(false);
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
