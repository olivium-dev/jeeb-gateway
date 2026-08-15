namespace JeebGateway.Tracking;

/// <summary>
/// MVP location store. Holds the latest reported position per Jeeber.
///
/// <para><b>The store reports; it does not judge.</b> A read returns the last
/// fix on record together with its <see cref="StoredPosition.ReceivedAt"/> stamp,
/// and the CALLER decides whether that fix is fresh enough for its purpose (see
/// <see cref="TrackingFreshness"/>). The store only forgets a fix once it passes
/// <see cref="TrackingOptions.PositionRetention"/>, which is a memory bound, not a
/// freshness verdict.</para>
///
/// <para><b>Production swap: Redis.</b>
/// <c>SET jeeber:{id}:position &lt;json&gt; EX &lt;PositionRetention&gt;</c> (default
/// 43200 s) keyed per Jeeber, so multiple gateway replicas share the view. The
/// <c>EX</c> maps to <see cref="TrackingOptions.PositionRetention"/> and NOT to
/// <see cref="TrackingOptions.PositionTtl"/>: the serialised value carries
/// <c>receivedAt</c>, so freshness is derived from the value at read time and
/// never from key existence. Pinning <c>EX</c> at the old 300 s would make a
/// Redis-backed replica answer <c>nil</c> for a courier we have merely lost —
/// wire-identical to a courier who never reported — which is precisely the
/// collapse this contract was changed to eliminate.</para>
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
    /// shared ASP.NET thread pool. The former in-memory store
    /// stays fully in-memory (returns a completed task), so the async signature adds
    /// no cost on the default path.
    /// </remarks>
    Task<LocationStoreUpdateResult> RecordAsync(string jeeberId, IReadOnlyList<GpsPointDto> points, CancellationToken ct = default);

    /// <summary>
    /// Read the most recent fix on record for the Jeeber, <b>regardless of its
    /// age</b>. Returns <c>null</c> ONLY when nothing is on record — either no fix
    /// was ever recorded, or the last one passed
    /// <see cref="TrackingOptions.PositionRetention"/> and was dropped to bound
    /// memory. The in-memory implementation stays lock-free; the upstream
    /// implementation awaits the geolocation-service (see the remarks on
    /// <see cref="RecordAsync"/> for why this path is async).
    /// </summary>
    /// <remarks>
    /// <b>This used to return <c>null</c> for any fix older than
    /// <see cref="TrackingOptions.PositionTtl"/>, and that was the phantom-pin
    /// defect.</b> Discarding the fix discarded its <c>ReceivedAt</c> stamp, which
    /// is the only evidence the staleness contract has: with it gone, the snapshot
    /// endpoint could not tell "no courier has reported yet" from "the courier we
    /// were tracking is missing", and reported the latter as
    /// <c>stale:false</c> — an all-clear for the worst state on the axis. Callers
    /// that genuinely need a CURRENT fix (dispute evidence, for example) must now
    /// say so explicitly via <see cref="TrackingFreshness.Classify"/> rather than
    /// leaning on the store to have silently thrown the old one away.
    /// </remarks>
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
