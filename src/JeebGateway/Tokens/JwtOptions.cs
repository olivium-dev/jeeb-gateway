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
}
