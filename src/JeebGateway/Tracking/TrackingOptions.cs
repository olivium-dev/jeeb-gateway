namespace JeebGateway.Tracking;

/// <summary>
/// Tunable thresholds for T-backend-014. Values are mirrored in
/// appsettings under the <c>Tracking</c> section; defaults match the
/// acceptance criteria (5-min TTL, 5s SSE cadence, 2-min stale window).
/// </summary>
public class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>
    /// How long a recorded position survives in the in-memory store
    /// before it is treated as expired. Matches the production Redis
    /// EXPIRE — keep the two in lockstep when the in-memory shim is
    /// replaced.
    /// </summary>
    public TimeSpan PositionTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Interval the SSE endpoint pushes a frame at. Default 5 seconds
    /// per AC. Tests override to a smaller value so they don't wait the
    /// full 5 seconds per assertion.
    /// </summary>
    public TimeSpan SseInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// When the most recent sample is older than this, the SSE stream
    /// switches the event name to <c>last-seen</c>. Default 2 minutes
    /// per AC.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cap on the number of points the server accepts in a single batch.
    /// Defends against malformed or runaway clients; well above any
    /// reasonable mobile batching cadence at 50k updates/min.
    /// </summary>
    public int MaxPointsPerBatch { get; set; } = 200;
}
