namespace JeebGateway.Availability;

/// <summary>
/// S06 / ADR-HB-001 cutover flag for the jeeber-availability presence source.
///
/// <para>
/// When <see cref="Enabled"/> is true,
/// <see cref="JeebGateway.Controllers.AvailabilityController"/> reads/writes the
/// jeeber's online bit + recency watermark through the NEW reusable
/// <c>heart-beat</c> service
/// (<see cref="JeebGateway.Services.Clients.IHeartBeatServiceClient"/>) instead of
/// the delivery-service presence store. The public
/// <c>GET/PATCH /jeebers/me/availability</c> response shape is byte-identical
/// either way, so no S06 contract assertion shifts and S01–S04 are untouched.
/// </para>
///
/// <para>
/// Default <b>false</b> in EVERY environment this round — including
/// <c>appsettings.Production.json</c> — because heart-beat is NOT yet deployed.
/// While off, the availability surface keeps using the live delivery-service
/// presence wire (zero behaviour change). Flip via
/// <c>FeatureFlags__Heartbeat__Enabled=true</c> (a deploy
/// <c>workflow_dispatch</c> input), staging-first, ONLY after heart-beat is live
/// and its <c>/health/ready</c> + a real <c>PATCH /v1/presence</c> round-trip
/// smoke-pass. This mirrors the cdn / contract-signing / kyc net-new
/// kill-switch shape — a config-only cutover with the delivery path as the
/// instant rollback target.
/// </para>
///
/// <para>
/// This is a SOURCE-SELECTION flag, deliberately NOT folded into
/// <see cref="JeebGateway.Services.UpstreamFeatureFlags"/> (which toggles
/// individual upstream proxies on/off) — it selects which presence BACKEND the
/// one availability surface reads, so it lives in its own section the way
/// <c>FeatureFlags:DurableRequests</c> does.
/// </para>
/// </summary>
public sealed class HeartbeatFeatureOptions
{
    public const string SectionName = "FeatureFlags:Heartbeat";

    /// <summary>
    /// Master switch. Default <c>false</c> = today's green delivery-service
    /// presence path. When true the availability surface routes through
    /// heart-beat.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// OPAQUE consumer-namespace string forwarded to heart-beat as
    /// <c>roleKey</c> on the go-online write. heart-beat stores it but never
    /// interprets it; it carries no Jeeb domain meaning on the wire. Kept
    /// configurable so a future consumer can namespace its online-set without a
    /// code change; the Jeeb default is the opaque literal <c>"jeeber"</c>.
    /// </summary>
    public string RoleKey { get; init; } = "jeeber";
}
