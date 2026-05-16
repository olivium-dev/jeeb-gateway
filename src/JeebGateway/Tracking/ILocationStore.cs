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
    LocationStoreUpdateResult Record(string jeeberId, IReadOnlyList<GpsPointDto> points);

    /// <summary>
    /// Read the most recent non-expired fix for the Jeeber. Returns
    /// <c>null</c> when no fix has been recorded or the latest fix
    /// has aged out past the TTL. Optimised for lock-free reads — the
    /// 50k updates/min target depends on this path being non-blocking.
    /// </summary>
    StoredPosition? GetLatest(string jeeberId);
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
