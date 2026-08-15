using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Generated = JeebGateway.Services.Generated.GeolocationService;
using GeneratedClient = JeebGateway.Services.Generated.GeolocationService.IGeolocationServiceClient;

namespace JeebGateway.Tracking;

/// <summary>
/// Upstream-backed <see cref="ILocationStore"/> that delegates to the shared,
/// product-agnostic geolocation-service (Python / FastAPI) via the NSwag-shaped
/// <see cref="GeneratedClient"/>. Since W3-19 this is the ONLY
/// <see cref="ILocationStore"/>: the in-memory store and its
/// <c>FeatureFlags:UseUpstream:Geolocation</c> switch are deleted, because a gateway
/// that can hold positions in process memory is a gateway that owns data. Neither the
/// controller nor the SSE loop ever branched on the flag, so removing it changes no
/// call site.
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
///   to <c>null</c> (no fix on record), NOT an exception. An old fix is returned AS
///   IS with its <c>created_at</c> as <see cref="StoredPosition.ReceivedAt"/> —
///   age is reported, never swallowed — and only a fix past
///   <see cref="TrackingOptions.PositionRetention"/> maps to <c>null</c>, mirroring
///   the in-memory store's retention bound so callers see identical behaviour
///   regardless of the flag.</item>
/// </list>
///
/// <para><b>Do not re-add a <see cref="TrackingOptions.PositionTtl"/> filter here.</b>
/// This wrapper used to map any fix older than the 5-minute TTL to <c>null</c> to
/// mirror the in-memory store. Both halves of that mirror were the phantom-pin
/// defect: a swallowed fix takes its <c>ReceivedAt</c> with it, and the snapshot
/// endpoint then cannot distinguish "the courier never started" from "the courier
/// is missing" — see <see cref="ILocationStore.GetLatestAsync"/>. Freshness is
/// classified by the caller (<see cref="TrackingFreshness"/>), which is what keeps
/// <c>FeatureFlags:UseUpstream:Geolocation</c> behaviourally invisible.</para>
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

        var response = await GuardAsync(
            () => _client.UpdateLocationAsync(body, ct), nameof(RecordAsync), jeeberId).ConfigureAwait(false);

        var latest = MapLatest(response.Latest);
        return new LocationStoreUpdateResult(response.Accepted, response.Rejected, latest);
    }

    public async Task<StoredPosition?> GetLatestAsync(string jeeberId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(jeeberId)) return null;

        var upstream = await GuardAsync(
            () => _client.GetUserLocationAsync(jeeberId, ct), nameof(GetLatestAsync), jeeberId).ConfigureAwait(false);
        if (upstream is null)
        {
            // 404 from /locations/user/{id} == no fix, not an error.
            return null;
        }

        var receivedAt = upstream.CreatedAt ?? _clock.GetUtcNow();
        if (_clock.GetUtcNow() - receivedAt > _options.CurrentValue.PositionRetention)
        {
            // Past RETENTION (not the freshness TTL) the fix is forgotten entirely,
            // matching the freshness window the deleted in-memory store used. Anything younger is returned
            // with its age intact so the caller can classify it as live / stale /
            // lost — see the class remarks for why filtering on PositionTtl here was
            // the bug.
            return null;
        }

        return new StoredPosition(
            upstream.Latitude,
            upstream.Longitude,
            Accuracy: null,
            DeviceTimestamp: receivedAt,
            ReceivedAt: receivedAt);
    }

    /// <summary>Translates the generated ApiException into the domain failure the pipeline
    /// maps to 503. 401/403 is OUR credential refused, logged as operator-actionable (W3-18).</summary>
    private async Task<T> GuardAsync<T>(Func<Task<T>> call, string member, string jeeberId)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (Generated.ApiException ex)
        {
            if (ex.StatusCode is 401 or 403)
            {
                _logger.LogError(ex,
                    "geolocation-service REFUSED THE GATEWAY CREDENTIAL ({Status}) on {Member} for {JeeberId}: GPS fixes are being dropped. Check the gateway's geolocation-service token/role map.",
                    ex.StatusCode, member, jeeberId);
            }
            else
            {
                _logger.LogError(ex,
                    "geolocation-service returned {Status} on {Member} for {JeeberId}: GPS fixes are being dropped.",
                    ex.StatusCode, member, jeeberId);
            }

            throw new LocationUpstreamUnavailableException(member, ex.StatusCode, ex);
        }
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
