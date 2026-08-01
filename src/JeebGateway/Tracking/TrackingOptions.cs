namespace JeebGateway.Tracking;

/// <summary>
/// Tunable thresholds for T-backend-014. Values are mirrored in
/// appsettings under the <c>Tracking</c> section; defaults match the
/// acceptance criteria (2-min stale window, 5-min "lost" window).
///
/// <para><c>SseInterval</c> (default 5 s) is GONE. It was the sleep of the
/// gateway's server-side re-read loop, not a cadence anyone chose — see
/// <c>LocationController</c>. Deleting the option as well as the loop means
/// re-introducing the poll cannot be done by flipping config.</para>
///
/// <para><b>The three windows are a strict ladder</b> and must satisfy
/// <c>0 &lt; StaleThreshold &lt;= PositionTtl &lt; PositionRetention</c>. That
/// inequality is enforced at startup (see the <c>AddOptions&lt;TrackingOptions&gt;</c>
/// registration in <c>Program.cs</c>) because violating it is exactly how the
/// phantom-courier-pin defect is re-introduced by configuration alone — see the
/// remarks on <see cref="PositionRetention"/>.</para>
/// </summary>
public class TrackingOptions
{
    public const string SectionName = "Tracking";

    /// <summary>
    /// Age past which a recorded position is no longer treated as the Jeeber's
    /// current whereabouts: the snapshot reports <c>positionStatus: "lost"</c>,
    /// keeps <c>stale: true</c>, and stops publishing the coordinates.
    ///
    /// <para><b>This is a CLASSIFICATION threshold, not an eviction deadline.</b>
    /// It used to be both, and that conflation was the bug: <c>GetLatest</c>
    /// deleted the fix on read once it crossed this line, which destroyed the
    /// very fact the staleness contract needs, so <c>stale</c> silently
    /// regressed from <c>true</c> back to <c>false</c> the moment the courier
    /// had been missing longest. Eviction is now governed solely by
    /// <see cref="PositionRetention"/>; this value only decides how a retained
    /// fix is *reported*.</para>
    /// </summary>
    public TimeSpan PositionTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a recorded position is RETAINED by the store before it is
    /// dropped to bound memory. Must be comfortably larger than the longest
    /// plausible delivery so that "we have lost the courier" stays reportable
    /// for the whole trip rather than collapsing into "the courier never
    /// started".
    ///
    /// <para><b>This is the value the production Redis <c>EX</c> maps to</b> —
    /// <c>SET jeeber:{id}:position &lt;json&gt; EX 43200</c>. It is NOT
    /// <see cref="PositionTtl"/> any more. Keeping <c>EX</c> at the old 300 s
    /// would make a Redis-backed store answer <c>nil</c> for a courier we have
    /// merely lost, which is indistinguishable from a courier who never
    /// reported — the exact wire collapse this option exists to prevent.
    /// Freshness is derived at READ time from the <c>receivedAt</c> stamp
    /// carried inside the value, never from key existence.</para>
    /// </summary>
    public TimeSpan PositionRetention { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// When the most recent sample is older than this, the tracking snapshot
    /// reports <c>stale: true</c> and a <c>secondsSinceUpdate</c>, so the
    /// client renders the "Jeeber offline" affordance. Default 2 minutes
    /// per AC. Once <c>stale</c> goes true it never goes back to false for the
    /// same fix — it is monotonic in the fix's age.
    /// </summary>
    public TimeSpan StaleThreshold { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Cap on the number of points the server accepts in a single batch.
    /// Defends against malformed or runaway clients; well above any
    /// reasonable mobile batching cadence at 50k updates/min.
    /// </summary>
    public int MaxPointsPerBatch { get; set; } = 200;
}
