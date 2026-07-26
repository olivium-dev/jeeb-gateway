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

    /// <summary>Per-recipient relay timeout.</summary>
    public TimeSpan PerSendTimeout { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Whole-job budget; bounds the fan-out even with a wedged relay.</summary>
    public TimeSpan TotalBudget { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Escape hatch: blast the legacy topic when the resolved recipient set is EMPTY.
    /// Default FALSE — ON re-opens the exact leak P1 closes (a topic send reaches every
    /// subscriber, including the initiator) and must never be enabled on MSI.
    /// </summary>
    public bool TopicFallbackWhenEmpty { get; set; }

    /// <summary>Bounded-channel capacity for the off-hot-path dispatch buffer.</summary>
    public int QueueCapacity { get; set; } = 256;
}
