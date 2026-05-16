using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// Wires the gateway's two-tier rate-limit policy (T-backend-032):
///   - per-user partition  (JWT sub or X-User-Id header) — 100 req / min
///   - per-IP   partition  (RemoteIpAddress, falls back to X-Forwarded-For
///                          left-most when behind the BFF edge) — 1000 req / min
///
/// The two limiters are chained via <see cref="PartitionedRateLimiter.CreateChained{TResource}"/>.
/// A single request consumes one permit in each partition; if either is
/// exhausted the request rejects with 429 + Retry-After.
///
/// Auth routes (<c>/auth/tokens/*</c>) bypass the user partition (no identity
/// before tokens issue) but stay subject to the per-IP cap, which is the
/// correct knob against OTP / refresh-token brute force.
/// </summary>
public static class RateLimitingExtensions
{
    public const string UserPartition = "user";
    public const string IpPartition = "ip";

    public static IServiceCollection AddJeebRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = (int)HttpStatusCode.TooManyRequests;

            options.OnRejected = async (ctx, ct) =>
            {
                // Surface the limiter's own retry-after when present; the 60s
                // ceiling matches the 1-minute window — clients should not poll faster.
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

            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                BuildUserLimiter(services),
                BuildIpLimiter(services));
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
                // Anonymous traffic is constrained only by the IP partition.
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

    private static string? ResolveUserId(HttpContext ctx)
    {
        var sub = ctx.User?.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? ctx.User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(sub)) return sub;

        if (ctx.Request.Headers.TryGetValue("X-User-Id", out var hdr)
            && !string.IsNullOrWhiteSpace(hdr))
        {
            return hdr.ToString();
        }
        return null;
    }

    private static string? ResolveClientIp(HttpContext ctx)
    {
        // Behind an edge proxy the gateway sees the proxy's IP on
        // Connection.RemoteIpAddress; rely on X-Forwarded-For when present.
        if (ctx.Request.Headers.TryGetValue("X-Forwarded-For", out var fwd)
            && !string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        return ctx.Connection.RemoteIpAddress?.ToString();
    }
}
