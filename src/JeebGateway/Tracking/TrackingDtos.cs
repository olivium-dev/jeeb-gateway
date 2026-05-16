namespace JeebGateway.Tracking;

/// <summary>
/// A single GPS sample reported by a Jeeber client. Mirrors the wire shape
/// captured in T-backend-014: latitude/longitude in WGS84 degrees, accuracy
/// in metres (best-effort from the device sensor fusion stack), and the
/// timestamp the device observed the fix.
/// </summary>
public class GpsPointDto
{
    public required double Lat { get; init; }
    public required double Lng { get; init; }

    /// <summary>
    /// Horizontal accuracy in metres as reported by the device. Optional —
    /// when absent the point is still accepted and the in-memory store
    /// records a NaN sentinel so downstream consumers can decide whether
    /// to factor it into route smoothing.
    /// </summary>
    public double? Accuracy { get; init; }

    /// <summary>
    /// When the device observed the fix. The store uses the most recent
    /// timestamp from the batch as the Jeeber's "last seen" reference for
    /// stale detection (AC: stale &gt; 2 min).
    /// </summary>
    public required DateTimeOffset Timestamp { get; init; }
}

/// <summary>
/// POST /location/update body. Jeebers stream batches of points (typical
/// cadence: every few seconds, batched on lossy networks) so the server
/// can amortise the per-RPC overhead at 50k updates/min.
/// </summary>
public class LocationUpdateRequest
{
    public List<GpsPointDto>? Points { get; set; }
}

/// <summary>
/// POST /location/update response — confirms how many points were
/// accepted and echoes the latest-known position for the Jeeber.
/// </summary>
public class LocationUpdateResponse
{
    public required int Accepted { get; init; }
    public required int Rejected { get; init; }
    public GpsPointDto? Latest { get; init; }
}

/// <summary>
/// GET /deliveries/{id}/tracking emits one of these as the data payload of
/// each SSE event. The event name (<c>position</c> vs <c>last-seen</c>)
/// determines which branch the client UI takes; the polyline is included
/// on the position events so the client doesn't need a separate request.
/// </summary>
public class TrackingFrameDto
{
    public required string DeliveryId { get; init; }
    public required string JeeberId { get; init; }

    /// <summary>
    /// The most recent position recorded for the Jeeber, or null when the
    /// store has nothing yet (the SSE stream emits a single
    /// <c>position</c> frame with a null position so the client can paint
    /// an initial "awaiting first ping" state).
    /// </summary>
    public GpsPointDto? Position { get; init; }

    /// <summary>
    /// Straight-line route from the current position to the dropoff
    /// (MVP polyline). Encoded as an ordered list of [lat, lng] pairs.
    /// Empty when the dropoff is unknown or the Jeeber position is
    /// unavailable.
    /// </summary>
    public IReadOnlyList<double[]> Polyline { get; init; } = Array.Empty<double[]>();

    /// <summary>
    /// True when the most recent sample is older than the stale
    /// threshold (default 2 min). The SSE stream switches the event name
    /// to <c>last-seen</c> in that case but still carries the same DTO
    /// so the client has the last known fix.
    /// </summary>
    public bool Stale { get; init; }

    /// <summary>
    /// Seconds elapsed since the most recent sample. Null when nothing
    /// has been recorded yet.
    /// </summary>
    public double? SecondsSinceUpdate { get; init; }

    public required DateTimeOffset ServerTimestamp { get; init; }
}
