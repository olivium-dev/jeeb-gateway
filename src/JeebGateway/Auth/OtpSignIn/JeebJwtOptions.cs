using System.ComponentModel.DataAnnotations;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// JWT options for the T-BE-001 / JEB-471 OTP sign-in path. Distinct from the
/// legacy <see cref="JeebGateway.Tokens.JwtOptions"/> because that surface uses
/// HS256 with 15-min access tokens (T-backend-043); the OTP path is LOCKED by
/// audit comment #14764 to HS512 with 1h access + 30d refresh and a separate
/// signing key sourced exclusively from environment / secrets (JeebJwt:*).
/// </summary>
public sealed class JeebJwtOptions
{
    public const string SectionName = "JeebJwt";

    /// <summary>
    /// HMAC-SHA512 signing key. MUST be ≥ 64 bytes (HS512 requires key size
    /// ≥ hash output size = 512 bits = 64 bytes; the runtime check lives in
    /// <see cref="JeebJwtIssuer"/>). Loaded from the
    /// <c>JeebJwt:SigningKey</c> environment variable or sealed secret;
    /// never from <c>appsettings.json</c> (AC5).
    /// </summary>
    [Required]
    [MinLength(64)]
    public string SigningKey { get; set; } = string.Empty;

    [Required]
    public string Issuer { get; set; } = "https://auth.jeeb.lb";

    [Required]
    public string Audience { get; set; } = "jeeb-mobile";

    /// <summary>Access token TTL. Locked at 1 hour (3600s) by AC5b.</summary>
    [Range(60, 86_400)]
    public int AccessTtlSeconds { get; set; } = 3600;

    /// <summary>Refresh token TTL. Locked at 30 days (2_592_000s) by AC5b.</summary>
    [Range(60, 31_536_000)]
    public int RefreshTtlSeconds { get; set; } = 2_592_000;
}

/// <summary>
/// Gateway-side rate limit policy for <c>POST /v1/auth/otp/request</c>.
/// Locked by audit #14764:
///   <list type="bullet">
///     <item>10 requests / minute / source-IP</item>
///     <item>3 requests / minute / normalized phone</item>
///   </list>
/// Excess returns 429 + ProblemDetails type=<c>rate_limited</c>
/// (AC-GatewayRateLimit).
/// </summary>
public sealed class GatewayRateLimitOptions
{
    public const string SectionName = "GatewayRateLimit";

    [Range(1, 1000)]
    public int PerPhonePerMin { get; set; } = 3;

    [Range(1, 10_000)]
    public int PerIpPerMin { get; set; } = 10;
}

/// <summary>
/// Base URLs of the downstream contracts consumed by the OTP sign-in path.
/// </summary>
public sealed class UserManagementApiOptions
{
    public const string SectionName = "UserManagementApi";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}

public sealed class ServiceOtpApiOptions
{
    public const string SectionName = "ServiceOTPApi";

    [Required]
    public string BaseUrl { get; set; } = string.Empty;
}
