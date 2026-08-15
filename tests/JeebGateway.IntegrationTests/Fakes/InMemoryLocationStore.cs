// W3-19: this was src/JeebGateway/Tracking/InMemoryLocationStore.cs. It is a TEST
// DOUBLE now, not production code — geolocation-service is the only location store
// the gateway registers. Kept because the TTL-vs-retention semantics below are worth
// testing and the tests need an ILocationStore they can drive deterministically;
// deleted from the artifact because a gateway that can hold positions in process
// memory is a gateway that owns data.

using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace JeebGateway.Tracking;

/// <summary>
/// Lock-free in-memory location store. Reads are a single
/// <see cref="ConcurrentDictionary{TKey, TValue}.TryGetValue"/> followed
/// by a TTL check, which is the hot path for the SSE endpoint and
/// matters for the 50k updates/min budget. Writes use
/// <see cref="ConcurrentDictionary{TKey, TValue}.AddOrUpdate"/> with a
/// pure update delegate so concurrent writers serialise per-key without
/// blocking other keys or readers.
///
/// Production swap: Redis (single SET ... EX) keyed on
/// <c>jeeber:{id}:position</c>. The contract on <see cref="ILocationStore"/>
/// is identical so the controller and SSE loop don't change.
///
/// <para><b>The Redis <c>EX</c> is <see cref="TrackingOptions.PositionRetention"/>
/// (default 43200 s), not <see cref="TrackingOptions.PositionTtl"/>.</b> Key
/// expiry is a memory bound only. Freshness is derived at read time from the
/// <c>receivedAt</c> stamp inside the value, exactly as
/// <see cref="GetLatest"/> does here, so the in-memory and Redis
/// implementations agree state-for-state. Setting <c>EX 300</c> would restore
/// the phantom-pin defect on the Redis path alone, silently and only in
/// production — see <see cref="GetLatest"/>.</para>
/// </summary>
public class InMemoryLocationStore : ILocationStore
{
    private readonly ConcurrentDictionary<string, StoredPosition> _positions =
        new(StringComparer.Ordinal);
    private readonly IOptionsMonitor<TrackingOptions> _options;
    private readonly TimeProvider _clock;

    public InMemoryLocationStore(IOptionsMonitor<TrackingOptions> options, TimeProvider clock)
    {
        _options = options;
        _clock = clock;
    }

    // JEBV4-57: async seam over the lock-free in-memory core. The work is
    // synchronous and non-blocking, so we return an already-completed task —
    // zero thread-pool cost on the flag-OFF default path.
    public Task<LocationStoreUpdateResult> RecordAsync(string jeeberId, IReadOnlyList<GpsPointDto> points, CancellationToken ct = default)
        => Task.FromResult(Record(jeeberId, points));

    public Task<StoredPosition?> GetLatestAsync(string jeeberId, CancellationToken ct = default)
        => Task.FromResult(GetLatest(jeeberId));

    private LocationStoreUpdateResult Record(string jeeberId, IReadOnlyList<GpsPointDto> points)
    {
        if (string.IsNullOrEmpty(jeeberId)) throw new ArgumentException("jeeberId required", nameof(jeeberId));
        if (points is null || points.Count == 0) return new LocationStoreUpdateResult(0, 0, GetLatest(jeeberId));

        var now = _clock.GetUtcNow();
        var accepted = 0;
        var rejected = 0;
        StoredPosition? newest = null;

        foreach (var p in points)
        {
            if (!IsValidPoint(p))
            {
                rejected++;
                continue;
            }
            accepted++;
            if (newest is null || p.Timestamp > newest.DeviceTimestamp)
            {
                newest = new StoredPosition(p.Lat, p.Lng, p.Accuracy, p.Timestamp, now);
            }
        }

        if (newest is null)
        {
            return new LocationStoreUpdateResult(accepted, rejected, GetLatest(jeeberId));
        }

        // AddOrUpdate with a pure delegate so concurrent writers serialise
        // per-key. We keep whichever device-timestamp is newer to defend
        // against out-of-order delivery on lossy mobile networks.
        var stored = _positions.AddOrUpdate(
            jeeberId,
            addValueFactory: _ => newest,
            updateValueFactory: (_, existing) =>
                newest.DeviceTimestamp >= existing.DeviceTimestamp ? newest : existing);

        return new LocationStoreUpdateResult(accepted, rejected, stored);
    }

    /// <summary>
    /// Returns the last fix on record for the Jeeber regardless of its age, or
    /// <c>null</c> when there is none. Freshness is the caller's call — see
    /// <see cref="TrackingFreshness.Classify"/>.
    ///
    /// <para><b>Why this no longer evicts at <see cref="TrackingOptions.PositionTtl"/>.</b>
    /// It used to: a read that found a fix older than the 300 s TTL deleted it and
    /// returned <c>null</c>. That destroyed the fix's <c>ReceivedAt</c> stamp,
    /// which is the only thing the snapshot endpoint can compute staleness from,
    /// so the wire regressed from "stale:true, with a position" (120–300 s) to
    /// "no position, and everything is fine" (past 300 s). A customer's map kept
    /// the last marker it had drawn, was told nothing was wrong, and showed a
    /// phantom courier pin at a location the courier had left minutes earlier. The
    /// trigger needs no failure at all: <c>distanceFilter: 10</c> on the mobile
    /// uploader means a stationary courier legitimately uploads nothing for
    /// minutes.</para>
    ///
    /// <para>Eviction now happens only past
    /// <see cref="TrackingOptions.PositionRetention"/>, a memory bound set far
    /// beyond any delivery, so within a trip the fact is always available to
    /// report. It stays LAZY (on read) rather than moving to a hosted sweeper: the
    /// key space is bounded by the number of distinct Jeebers seen in one process
    /// lifetime — fleet-sized, a few tens of bytes each — and <c>Record</c>
    /// overwrites in place, so there is no per-Jeeber growth for a background
    /// sweeper to reclaim.</para>
    /// </summary>
    public StoredPosition? GetLatest(string jeeberId)
    {
        if (!_positions.TryGetValue(jeeberId, out var fix)) return null;
        var retention = _options.CurrentValue.PositionRetention;
        if (_clock.GetUtcNow() - fix.ReceivedAt > retention)
        {
            // Lazy eviction: don't block readers on a sweeper. We only
            // remove the specific entry we observed — a concurrent
            // writer that put a fresher value in between TryGetValue
            // and the remove won't be clobbered by the conditional
            // ICollection contract.
            ((ICollection<KeyValuePair<string, StoredPosition>>)_positions)
                .Remove(new KeyValuePair<string, StoredPosition>(jeeberId, fix));
            return null;
        }
        return fix;
    }

    private static bool IsValidPoint(GpsPointDto p) =>
        p is not null
        && p.Lat is >= -90 and <= 90
        && p.Lng is >= -180 and <= 180
        && !double.IsNaN(p.Lat)
        && !double.IsNaN(p.Lng)
        && p.Timestamp != default;
}
