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

    public StoredPosition? GetLatest(string jeeberId)
    {
        if (!_positions.TryGetValue(jeeberId, out var fix)) return null;
        var ttl = _options.CurrentValue.PositionTtl;
        if (_clock.GetUtcNow() - fix.ReceivedAt > ttl)
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
