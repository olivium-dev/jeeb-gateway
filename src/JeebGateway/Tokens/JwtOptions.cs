namespace JeebGateway.Tokens;

/// <summary>
/// JWT token rotation policy for T-backend-043. Bound from the "Jwt"
/// configuration section. Defaults encode the MVP security policy:
///   - access  token: 15 minutes (short-lived, no revocation list lookup)
///   - refresh token: 30 days, single-use, rotated on every refresh
/// </summary>
public class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "jeeb-gateway";
    public string Audience { get; set; } = "jeeb-clients";

    /// <summary>
    /// HMAC-SHA256 signing key. MUST be at least 32 bytes (256 bits).
    /// Production wiring will inject this from a sealed secret; the
    /// default below is only used by tests and local development.
    /// </summary>
    public string SigningKey { get; set; } = "dev-only-signing-key-32-bytes-minimum!!";

    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>
    /// JEBV4-260 — bounded rotation grace window (seconds). When a refresh token
    /// is presented but was already rotated by a concurrent winner within this
    /// window, the loser's request is treated as a benign concurrent double-refresh
    /// (a client that does not single-flight; queued duplicate refresh calls) and
    /// does NOT burn the token family — preserving the winner's freshly-issued
    /// session instead of silently logging it out. Genuine stale-token replay is
    /// still caught earlier (the already-revoked-at-load path) regardless of this
    /// window, and any rotation older than the window still burns the chain.
    /// Set to 0 to disable the grace window and restore the strict burn-on-race
    /// behavior. Kept deliberately small (standard OAuth rotation leeway).
    /// </summary>
    public int RefreshRotationGraceSeconds { get; set; } = 10;
}
