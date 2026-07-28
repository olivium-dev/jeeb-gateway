namespace JeebGateway.Notifications;

/// <summary>
/// P1 — knobs for the new-request "finding jeebers" fan-out
/// (<see cref="NewRequestPushNotifier"/>). Bound from configuration section
/// <c>Notifications:NewRequestFanout</c>; every default lives HERE in code, so no
/// appsettings change is needed to ship. MSI overrides, if ever needed, go via env
/// vars (<c>Notifications__NewRequestFanout__Enabled=false</c>).
/// </summary>
public class NewRequestFanoutOptions
{
    public const string SectionName = "Notifications:NewRequestFanout";

    /// <summary>
    /// Master switch. FALSE restores the legacy <c>jeeb_jeebers</c> topic blast verbatim —
    /// the config-only rollback path. Default TRUE.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// When no jeeber is currently online, fan out to the known-jeeber roster instead of
    /// notifying nobody. Guards the under-notification regression (R1). Default TRUE.
    /// </summary>
    public bool FallbackToKnownJeebers { get; set; } = true;

    /// <summary>How far back the known-jeeber roster reaches. Default 30 days.</summary>
    public TimeSpan KnownJeeberWindow { get; set; } = TimeSpan.FromDays(30);

    /// <summary>
    /// Geo narrowing around the request pickup point. NULL = disabled (the shipped default —
    /// turn on only after the staged enablement gate: MSI must show
    /// <c>jeeber_availability.last_location</c> populated for the live jeebers, and an E2E
    /// must prove a jeeber inside the radius still receives while one outside does not).
    /// Rows with no stored coordinates are ALWAYS kept, so a partially-populated table can
    /// never starve the fan-out.
    /// </summary>
    public double? RadiusKm { get; set; }

    /// <summary>Hard cap on recipients per request; the overflow is logged, not silently dropped.</summary>
    public int MaxRecipients { get; set; } = 500;

    /// <summary>Bounded parallelism so N sends cannot stampede the LAN-local relay (R9).</summary>
    public int MaxParallelSends { get; set; } = 8;

    /// <summary>
    /// Per-recipient relay timeout. Was a seat-local 2s, which every healthy send exceeded —
    /// measured 2.53-3.97s across 10 consecutive calls to registered recipients on
    /// 2026-07-28, i.e. 10 of 10 aborted. Now the shared
    /// <see cref="PushSendBudget.PerRecipient"/>; the distribution and the reasoning for 10s
    /// live there.
    /// </summary>
    public TimeSpan PerSendTimeout { get; set; } = PushSendBudget.PerRecipient;

    /// <summary>
    /// Whole-job budget; bounds the fan-out even with a wedged relay.
    ///
    /// <para>Raised 30s -> 60s alongside <see cref="PerSendTimeout"/>, because the two are
    /// not independent: the recipients a fan-out can fully cover in the worst case is
    /// <c>MaxParallelSends * floor(TotalBudget / PerSendTimeout)</c>. At 8 x floor(30/10) = 24
    /// that would have sat right on the observed roster size (23), so one extra jeeber joining
    /// would silently truncate the tail. 8 x floor(60/10) = 48 restores the head-room. Nothing
    /// waits on this job — it runs off the bounded channel in
    /// <c>NewRequestFanoutProcessor</c>, behind the create 201 — so the cost of the larger
    /// ceiling is bounded background time, not user-visible latency.</para>
    /// </summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Escape hatch: blast the legacy topic when the resolved recipient set is EMPTY.
    /// Default FALSE — ON re-opens the exact leak P1 closes (a topic send reaches every
    /// subscriber, including the initiator) and must never be enabled on MSI.
    /// </summary>
    public bool TopicFallbackWhenEmpty { get; set; }

    /// <summary>Bounded-channel capacity for the off-hot-path dispatch buffer.</summary>
    public int QueueCapacity { get; set; } = 256;
}
