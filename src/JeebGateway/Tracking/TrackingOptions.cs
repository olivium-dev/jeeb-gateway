namespace JeebGateway.Tracking;

/// <summary>
/// Tunable thresholds for T-backend-014. Values are mirrored in
/// appsettings under the <c>Tracking</c> section; defaults match the
/// acceptance criteria (5-min TTL, 2-min stale window).
///
/// <para><c>SseInterval</c> (default 5 s) is GONE. It was the sleep of the
/// gateway's server-side re-read loop, not a cadence anyone chose — see
/// <c>LocationController</c>. Deleting the option as well as the loop means
/// re-introducing the poll cannot be done by flipping config.</para>
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
    /// When the most recent sample is older than this, the tracking snapshot
    /// reports <c>stale: true</c> and a <c>secondsSinceUpdate</c>, so the
    /// client renders the "Jeeber offline" affordance. Default 2 minutes
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
