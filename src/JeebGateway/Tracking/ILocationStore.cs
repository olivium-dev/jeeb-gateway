namespace JeebGateway.Tracking;

/// <summary>
/// MVP location store. Holds the latest reported position per Jeeber
/// with a TTL. Production swap replaces the in-memory dictionary with
/// Redis (SET key value EX 300) so multiple gateway replicas share the
/// view.
/// </summary>
public interface ILocationStore
{
    /// <summary>
    /// Record one or more points for the Jeeber. Only the most recent
    /// (by device timestamp) is retained as the "latest" so out-of-order
    /// batches over lossy networks don't push an older fix on top of a
    /// newer one. Returns the number of points considered fresh and the
    /// resulting latest fix.
    /// </summary>
    /// <remarks>
    /// JEBV4-57 (GW12-PERF-1): this contract is ASYNC so the upstream-backed
    /// <see cref="GeoServiceLocationStore"/> can await the geolocation-service
    /// client directly — no <c>GetAwaiter().GetResult()</c> sync-over-async bridge
    /// on the GPS hot path (50k updates/min budget), so flipping
    /// <c>FeatureFlags:UseUpstream:Geolocation</c> on can no longer starve the
    /// shared ASP.NET thread pool. The flag-OFF <see cref="InMemoryLocationStore"/>
    /// stays fully in-memory (returns a completed task), so the async signature adds
    /// no cost on the default path.
    /// </remarks>
    Task<LocationStoreUpdateResult> RecordAsync(string jeeberId, IReadOnlyList<GpsPointDto> points, CancellationToken ct = default);

    /// <summary>
    /// Read the most recent non-expired fix for the Jeeber. Returns
    /// <c>null</c> when no fix has been recorded or the latest fix
    /// has aged out past the TTL. The in-memory implementation stays lock-free;
    /// the upstream implementation awaits the geolocation-service (see the
    /// remarks on <see cref="RecordAsync"/> for why this path is async).
    /// </summary>
    Task<StoredPosition?> GetLatestAsync(string jeeberId, CancellationToken ct = default);
}

/// <summary>
/// Single immutable position record. The <see cref="ReceivedAt"/> server
/// stamp is what stale detection compares against — the device clock
/// can be skewed, so the stale threshold is measured from when the
/// gateway received the sample.
/// </summary>
public sealed record StoredPosition(
    double Lat,
    double Lng,
    double? Accuracy,
    DateTimeOffset DeviceTimestamp,
    DateTimeOffset ReceivedAt);

public sealed record LocationStoreUpdateResult(int Accepted, int Rejected, StoredPosition? Latest);
