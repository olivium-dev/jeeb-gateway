namespace JeebGateway.Security;

/// <summary>
/// Strongly-typed configuration for the gateway's edge security middleware
/// (T-backend-032): CORS allow-list, rate-limit budgets, security-header
/// toggles, API key auth, and request validation. Bound from the "Security"
/// configuration section.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    public CorsConfig Cors { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
    public SecurityHeadersConfig Headers { get; set; } = new();
    public ApiKeyConfig ApiKey { get; set; } = new();
    public RequestValidationConfig RequestValidation { get; set; } = new();

    /// <summary>
    /// F3 security-P0 — privileged-caller gate on the credential-less token mint
    /// (<c>POST /auth/tokens</c>). The unauthenticated mint was an
    /// account-takeover-class backdoor: any caller could mint a gateway JWT for
    /// an arbitrary <c>userId</c> with no credential. This block requires an
    /// internal caller (test harness / CTO / a real auth flow) to present a
    /// privileged shared key before a token is minted.
    /// </summary>
    public TokenMintConfig TokenMint { get; set; } = new();

    /// <summary>
    /// SEC-C1 (Leg-11) — trusted-edge identity gate. Controls whether the gateway will
    /// honour client-supplied <c>X-User-Id</c> / <c>X-User-Roles</c> headers outside of
    /// Development/Testing. See <see cref="EdgeIdentityTrust"/>.
    /// </summary>
    public EdgeIdentityConfig EdgeIdentity { get; set; } = new();

    public class CorsConfig
    {
        /// <summary>Named CORS policy applied to every endpoint.</summary>
        public string PolicyName { get; set; } = "JeebAdminPanel";

        /// <summary>
        /// Allowed admin panel origins (scheme + host + port, no trailing slash).
        /// Defaults to the dev admin shell; production overrides via env / Vault.
        /// </summary>
        public string[] AllowedOrigins { get; set; } =
        {
            "http://localhost:5173",
            "http://localhost:3000"
        };

        public string[] AllowedMethods { get; set; } =
        {
            "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS"
        };

        public string[] AllowedHeaders { get; set; } =
        {
            "Authorization",
            "Content-Type",
            "X-Correlation-Id",
            "X-User-Id",
            "X-User-Roles",
            "X-Api-Key"
        };

        public string[] ExposedHeaders { get; set; } =
        {
            "X-Correlation-Id",
            "Retry-After",
            "X-RateLimit-Limit",
            "X-RateLimit-Remaining"
        };

        public bool AllowCredentials { get; set; } = true;
        public int PreflightMaxAgeSeconds { get; set; } = 600;
    }

    public class RateLimitConfig
    {
        /// <summary>Master switch; tests can disable to exercise N requests deterministically.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Per-authenticated-user budget — AC: 100 req / min / user.</summary>
        public int UserPermitsPerMinute { get; set; } = 100;

        /// <summary>Per-IP budget — AC: 1000 req / min / IP.</summary>
        public int IpPermitsPerMinute { get; set; } = 1000;

        /// <summary>Sliding window resolution (segments) for smoother bucketing.</summary>
        public int WindowSegments { get; set; } = 6;

        /// <summary>Token bucket: max burst capacity for the auth-route limiter.</summary>
        public int AuthTokenBucketLimit { get; set; } = 20;

        /// <summary>Token bucket: tokens replenished per period for the auth-route limiter.</summary>
        public int AuthTokensPerPeriod { get; set; } = 10;

        /// <summary>Token bucket: replenishment period for the auth-route limiter.</summary>
        public int AuthReplenishmentSeconds { get; set; } = 60;

        /// <summary>Fixed window: per-IP limit applied to sensitive endpoints (login, OTP).</summary>
        public int SensitiveEndpointPermitsPerWindow { get; set; } = 30;

        /// <summary>Fixed window: window duration in seconds for sensitive endpoints.</summary>
        public int SensitiveEndpointWindowSeconds { get; set; } = 60;
    }

    public class SecurityHeadersConfig
    {
        public bool Enabled { get; set; } = true;
        public int HstsMaxAgeSeconds { get; set; } = 31_536_000; // 1 year
        public bool HstsIncludeSubdomains { get; set; } = true;
        public bool HstsPreload { get; set; } = true;

        /// <summary>
        /// CSP for API responses: deny everything by default. Browsers that
        /// receive raw JSON ignore most CSP directives, but defence-in-depth
        /// against any HTML-injecting downstream regression is cheap.
        /// </summary>
        public string ContentSecurityPolicy { get; set; } =
            "default-src 'none'; frame-ancestors 'none'";
    }

    public class ApiKeyConfig
    {
        /// <summary>Master switch; disabled by default — enable in production via Vault.</summary>
        public bool Enabled { get; set; }

        /// <summary>Header name carrying the API key.</summary>
        public string HeaderName { get; set; } = "X-Api-Key";

        /// <summary>
        /// Mapping from service name to accepted key. Production values must come
        /// from secrets manager, never hardcoded in appsettings.
        /// </summary>
        public Dictionary<string, string> ServiceKeys { get; set; } = new();
    }

    /// <summary>
    /// F3 — gate on <c>POST /auth/tokens</c> (the credential-less mint).
    ///
    /// When <see cref="Enabled"/> is true (the production default), the mint
    /// requires the caller to present <see cref="HeaderName"/> carrying a value
    /// that matches <see cref="Key"/> (constant-time compared). A missing key
    /// yields 401; a present-but-wrong key yields 403.
    ///
    /// To avoid a hard dependency on a brand-new pipeline secret being present
    /// at deploy time (which would lock out the fleet's own verification path),
    /// <see cref="Key"/> may be left blank, in which case the gate falls back to
    /// the already-injected JWT signing key (<c>Jwt:SigningKey</c>). A caller who
    /// already holds the signing key can already forge any token, so reusing it
    /// as the privileged credential adds no new attack surface while closing the
    /// *unauthenticated* hole. Set a dedicated <see cref="Key"/> via
    /// <c>Security__TokenMint__Key</c> to rotate independently of the JWT key.
    /// </summary>
    public class TokenMintConfig
    {
        /// <summary>
        /// Master switch. Defaults to <c>true</c> so the mint is gated by
        /// default; tests and local dev may set it to <c>false</c> to exercise
        /// the issue/refresh/revoke mechanics without supplying the header.
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Header carrying the privileged-caller key.</summary>
        public string HeaderName { get; set; } = "X-Service-Auth-Key";

        /// <summary>
        /// Privileged shared key. When blank, the gate falls back to the JWT
        /// signing key (see class remarks). Never commit a real value — inject
        /// via <c>Security__TokenMint__Key</c> from a swarm secret.
        /// </summary>
        public string Key { get; set; } = string.Empty;
    }

    /// <summary>
    /// SEC-C1 — trusted-edge identity configuration. When <see cref="SharedSecret"/> is
    /// blank (the committed default), inbound <c>X-User-*</c> identity headers are NOT
    /// trusted in non-Development/Testing environments — identity derives from the verified
    /// JWT alone (fail closed). To re-enable an authenticated edge that legitimately injects
    /// identity, inject a non-empty secret via <c>Security__EdgeIdentity__SharedSecret</c>
    /// (from a swarm secret); the edge must then present it in <see cref="SharedSecretHeader"/>.
    /// Never commit a real secret value.
    /// </summary>
    public class EdgeIdentityConfig
    {
        /// <summary>Header the trusted edge presents to prove it is the edge.</summary>
        public string SharedSecretHeader { get; set; } = "X-Edge-Auth";

        /// <summary>
        /// Shared secret proving a request originates from the trusted edge. Blank by
        /// default → header identity is ignored outside Development/Testing (fail closed).
        /// </summary>
        public string SharedSecret { get; set; } = string.Empty;
    }

    public class RequestValidationConfig
    {
        /// <summary>Master switch for request validation middleware.</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>Maximum request body size in bytes (default 1 MB).</summary>
        public long MaxBodySizeBytes { get; set; } = 1_048_576;

        /// <summary>Maximum URL length in characters.</summary>
        public int MaxUrlLength { get; set; } = 2048;

        /// <summary>Maximum header value length in characters.</summary>
        public int MaxHeaderValueLength { get; set; } = 8192;

        /// <summary>Content types permitted for request bodies.</summary>
        public string[] AllowedContentTypes { get; set; } =
        {
            "application/json",
            "multipart/form-data",
            "application/x-www-form-urlencoded"
        };
    }
}
