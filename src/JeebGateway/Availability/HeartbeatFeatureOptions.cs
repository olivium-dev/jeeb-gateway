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

    /// <summary>
    /// S06 AUTH CONTRACT — the static service-auth key the gateway presents to
    /// heart-beat as the <c>X-Service-Auth-Key</c> header on every
    /// <c>PATCH /v1/presence</c> / <c>GET /v1/presence/{userId}</c> call.
    ///
    /// <para>
    /// heart-beat's auth middleware accepts EITHER this static
    /// <c>X-Service-Auth-Key</c> (constant-time compared against its own
    /// <c>HEARTBEAT_SERVICE_AUTH_KEY</c>) OR a JWKS-validated end-user JWT. The
    /// gateway is a BFF process — it has already authenticated the mobile user —
    /// so it authenticates to heart-beat as the trusted CALLER PROCESS via this
    /// static key, which grants the service-auth principal that may act on any
    /// <c>userId</c>. This is the minimal, secure, additive handshake: it reuses
    /// the path heart-beat already implements, requires no fleet-wide
    /// <c>ServiceAuth:Enabled</c> flip, and adds no new crypto.
    /// </para>
    ///
    /// <para>
    /// SECRET — never logged, never committed. Injected per environment from a
    /// swarm secret via <c>FeatureFlags__Heartbeat__ServiceAuthKey</c> (the SAME
    /// value the secrets engineer sets as heart-beat's
    /// <c>HEARTBEAT_SERVICE_AUTH_KEY</c> repo secret). Blank (the committed
    /// default) means the header is not attached — harmless while
    /// <see cref="Enabled"/> is false, since the gateway never dials heart-beat
    /// then. The flag MUST NOT be flipped on in any environment where this key is
    /// blank (heart-beat would 401); enforced by the smoke test that gates the
    /// cutover.
    /// </para>
    /// </summary>
    public string ServiceAuthKey { get; init; } = string.Empty;
}
