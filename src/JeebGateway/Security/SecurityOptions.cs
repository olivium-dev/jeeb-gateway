namespace JeebGateway.Security;

/// <summary>
/// Strongly-typed configuration for the gateway's edge security middleware
/// (T-backend-032): CORS allow-list, rate-limit budgets, and security-header
/// toggles. Bound from the "Security" configuration section.
/// </summary>
public class SecurityOptions
{
    public const string SectionName = "Security";

    public CorsConfig Cors { get; set; } = new();
    public RateLimitConfig RateLimit { get; set; } = new();
    public SecurityHeadersConfig Headers { get; set; } = new();

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
            "X-User-Roles"
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
}
