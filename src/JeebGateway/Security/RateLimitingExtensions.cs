using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Wires the gateway's multi-tier rate-limit policy (T-backend-032):
///
///   Global chained limiters (applied to all requests):
///   - per-user partition  (JWT sub or X-User-Id header) — sliding window, 100 req / min
///   - per-IP   partition  (RemoteIpAddress / X-Forwarded-For) — sliding window, 1000 req / min
///
///   Named policies (applied via [EnableRateLimiting] on controller / endpoint groups):
///   - "auth_token_bucket" — token bucket for auth routes (brute-force protection)
///   - "sensitive_fixed"   — fixed window for sensitive endpoints (login, OTP)
///   - "cdn_upload"        — fixed window for the anonymous 15 MB KYC upload proxy (CWE-770)
///
/// A single request consumes one permit in each applicable partition; if any
/// is exhausted the request rejects with 429 + Retry-After.
/// </summary>
public static class RateLimitingExtensions
{
    public const string UserPartition = "user";
    public const string IpPartition = "ip";
    public const string AuthTokenBucketPolicy = "auth_token_bucket";
    public const string SensitiveFixedPolicy = "sensitive_fixed";
    public const string CdnUploadPolicy = "cdn_upload";

    public static IServiceCollection AddJeebRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;

            options.OnRejected = async (ctx, ct) =>
            {
                TimeSpan retry = TimeSpan.FromSeconds(60);
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var ra))
                {
                    retry = ra;
                }

                ctx.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                ctx.HttpContext.Response.ContentType = "application/problem+json";
                await ctx.HttpContext.Response.WriteAsync(
                    """{"type":"https://httpstatuses.com/429","title":"Too Many Requests","status":429,"detail":"Rate limit exceeded. Retry after the duration in the Retry-After header."}""",
                    ct);
            };

            // Global: chained sliding-window limiters for user + IP.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                BuildUserLimiter(services),
                BuildIpLimiter(services));

            // Named policy: token bucket for auth routes (login / refresh).
            // Allows burst traffic up to the bucket limit, then drip-feeds permits.
            options.AddPolicy(AuthTokenBucketPolicy, httpContext =>
            {
                var opts = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
                    .CurrentValue.RateLimit;

                if (!opts.Enabled)
                    return RateLimitPartition.GetNoLimiter("disabled");

                var ip = ResolveClientIp(httpContext) ?? "unknown";
                return RateLimitPartition.GetTokenBucketLimiter(
                    partitionKey: $"auth_tb:{ip}",
                    factory: _ => new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = opts.AuthTokenBucketLimit,
                        TokensPerPeriod = opts.AuthTokensPerPeriod,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(opts.AuthReplenishmentSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            // Named policy: fixed window for sensitive endpoints (OTP, password reset).
            options.AddPolicy(SensitiveFixedPolicy, httpContext =>
            {
                var opts = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
                    .CurrentValue.RateLimit;

                if (!opts.Enabled)
                    return RateLimitPartition.GetNoLimiter("disabled");

                var ip = ResolveClientIp(httpContext) ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"sensitive_fw:{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = opts.SensitiveEndpointPermitsPerWindow,
                        Window = TimeSpan.FromSeconds(opts.SensitiveEndpointWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });

            // Named policy: fixed window for the ANONYMOUS KYC-photo upload proxy
            // (CWE-770 / API4:2023). CdnUploadProxyController is [AllowAnonymous] and
            // streams up to 15 MB per request; a tight per-IP budget bounds how much an
            // unauthenticated source can push through it, layered on TOP of the global
            // per-IP limiter. Partitioned by the trusted connection IP (ResolveClientIp
            // — never the raw X-Forwarded-For, per SEC-H1).
            options.AddPolicy(CdnUploadPolicy, httpContext =>
            {
                var opts = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
                    .CurrentValue.RateLimit;

                if (!opts.Enabled)
                    return RateLimitPartition.GetNoLimiter("disabled");

                var ip = ResolveClientIp(httpContext) ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: $"cdn_upload_fw:{ip}",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = opts.CdnUploadPermitsPerWindow,
                        Window = TimeSpan.FromSeconds(opts.CdnUploadWindowSeconds),
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    });
            });
        });

        return services;
    }

    private static PartitionedRateLimiter<HttpContext> BuildUserLimiter(IServiceCollection services)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var opts = httpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
                .CurrentValue.RateLimit;

            if (!opts.Enabled)
            {
                return RateLimitPartition.GetNoLimiter("disabled");
            }

            var userId = ResolveUserId(httpContext);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return RateLimitPartition.GetNoLimiter("anon");
            }

            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: $"{UserPartition}:{userId}",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = opts.UserPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = Math.Max(1, opts.WindowSegments),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        });
    }

    private static PartitionedRateLimiter<HttpContext> BuildIpLimiter(IServiceCollection services)
    {
        return PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        {
            var opts = httpContext.RequestServices
                .GetRequiredService<IOptionsMonitor<SecurityOptions>>()
                .CurrentValue.RateLimit;

            if (!opts.Enabled)
            {
                return RateLimitPartition.GetNoLimiter("disabled");
            }

            var ip = ResolveClientIp(httpContext) ?? "unknown";

            return RateLimitPartition.GetSlidingWindowLimiter(
                partitionKey: $"{IpPartition}:{ip}",
                factory: _ => new SlidingWindowRateLimiterOptions
                {
                    PermitLimit = opts.IpPermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    SegmentsPerWindow = Math.Max(1, opts.WindowSegments),
                    QueueLimit = 0,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    AutoReplenishment = true
                });
        });
    }

    internal static string? ResolveUserId(HttpContext ctx)
    {
        var sub = ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? ctx.User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(sub)) return sub;

        // SEC-C1: only trust the X-User-Id header for the per-user partition when it comes
        // from a trusted edge (or Development/Testing); otherwise a raw client could pick its
        // own partition key at will.
        if (EdgeIdentityTrust.HeadersTrusted(ctx)
            && ctx.Request.Headers.TryGetValue("X-User-Id", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr))
        {
            return hdr.ToString();
        }
        return null;
    }

    internal static string? ResolveClientIp(HttpContext ctx)
    {
        // SEC-H1: do NOT read the raw X-Forwarded-For header here. UseForwardedHeaders()
        // (Program.cs) already promotes X-Forwarded-For into Connection.RemoteIpAddress, but
        // ONLY when the immediate peer is in the ForwardedHeaders:KnownProxies/KnownNetworks
        // allowlist (the real trust boundary). Reading the raw header ourselves bypasses that
        // allowlist and lets any client spoof its rate-limit partition key — making every
        // per-IP limiter (global IP limiter, auth token bucket, sensitive fixed window, and
        // the OTP request limiter, which all call this method) trivially bypassable. Trust the
        // validated connection remote IP only.
        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
