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

    /// <summary>
    /// S09 (JEB-54): optional delivery scope. When present, the gateway
    /// resolves the delivery's parties via delivery-service and applies a
    /// participant gate (non-party Jeeber → 403, N2) plus a lifecycle gate
    /// (delivery not in the en-route phase → 409, N5) BEFORE the GPS point
    /// is recorded or any geolocation surface is touched. Absent on the
    /// legacy batch shape (<c>{points:[...]}</c>), which stays unchanged.
    /// </summary>
    public string? DeliveryId { get; set; }

    /// <summary>
    /// S09 single-point convenience shape: <c>{deliveryId, lat, lng}</c>.
    /// The delivery-scoped tracking client posts one fix at a time rather
    /// than a batch; when <see cref="Points"/> is empty and <see cref="Lat"/>/
    /// <see cref="Lng"/> are set, the gateway synthesises a single-point batch
    /// stamped with the server clock.
    /// </summary>
    public double? Lat { get; set; }

    /// <summary>See <see cref="Lat"/>.</summary>
    public double? Lng { get; set; }

    /// <summary>Optional horizontal accuracy (m) for the single-point shape.</summary>
    public double? Accuracy { get; set; }
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
/// JSON body of <c>GET /deliveries/{id}/tracking</c> — the ONLY shape this
/// surface returns (S09 H4/A3, JEB-54 AC3). A one-shot snapshot of the trip
/// route the client renders without holding a connection open.
///
/// <para>The former <c>TrackingFrameDto</c> — the payload of the deleted 5-second
/// server-side event-stream loop — is gone. Its two frame-only fields,
/// <see cref="Stale"/> and <see cref="SecondsSinceUpdate"/>, moved onto this DTO
/// so the "Jeeber offline" affordance survives without a stream. Both are
/// ADDITIVE on the wire: a client that does not read them is unaffected.</para>
/// </summary>
public class TrackingPolylineDto
{
    public required string DeliveryId { get; init; }
    public required string JeeberId { get; init; }

    /// <summary>
    /// Ordered [lat, lng] pairs from the latest Jeeber fix to the dropoff.
    /// Empty when either endpoint is unknown (no fix yet / no dropoff).
    /// </summary>
    public IReadOnlyList<double[]> Polyline { get; init; } = Array.Empty<double[]>();

    /// <summary>
    /// The most recent position recorded for the Jeeber, or null when the
    /// store has nothing yet.
    /// </summary>
    public GpsPointDto? Position { get; init; }

    /// <summary>
    /// True when the most recent sample is older than
    /// <c>Tracking:StaleThreshold</c> (default 2 min) — the client renders the
    /// "Jeeber offline" affordance. This replaces the deleted stream's
    /// <c>last-seen</c> event name.
    /// </summary>
    public bool Stale { get; init; }

    /// <summary>
    /// Seconds elapsed since the most recent sample, or null when nothing has
    /// been recorded yet.
    /// </summary>
    public double? SecondsSinceUpdate { get; init; }

    /// <summary>
    /// Stable hash of the polyline geometry. A repeat read whose route has
    /// not changed returns the same etag, so the client can skip a re-render
    /// and the geolocation Directions interface is not re-hit (JEB-54 AC3).
    /// </summary>
    public required string Etag { get; init; }

    public required DateTimeOffset ServerTimestamp { get; init; }
}
