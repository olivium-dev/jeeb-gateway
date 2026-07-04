using System.Net;
using FluentAssertions;
using JeebGateway.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Security;

/// <summary>
/// SEC-H1 (Leg-11) — the rate-limit client-IP resolver must NOT trust a raw, client-spoofable
/// X-Forwarded-For header. It is the single partition-key source for the global per-IP limiter,
/// the auth token-bucket, the sensitive fixed-window limiter, and the OTP request limiter; if it
/// honoured raw X-Forwarded-For, any client could pick a fresh partition per request and bypass
/// every per-IP cap (unlimited login / OTP / token attempts).
///
/// The trusted client address is <see cref="HttpContext.Connection"/>.RemoteIpAddress, which
/// UseForwardedHeaders() already populates from X-Forwarded-For ONLY for allow-listed proxies.
/// </summary>
public class XffRateLimitBypassTests
{
    [Fact]
    public void ResolveClientIp_Ignores_Spoofed_XForwardedFor()
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.7");
        ctx.Request.Headers["X-Forwarded-For"] = "1.2.3.4, 5.6.7.8";

        var ip = RateLimitingExtensions.ResolveClientIp(ctx);

        ip.Should().Be("10.0.0.7", "the resolver must use the validated connection remote IP");
        ip.Should().NotBe("1.2.3.4", "a raw client-supplied X-Forwarded-For must never win");
    }

    [Fact]
    public void ResolveClientIp_Spoofed_Xff_Values_Share_The_Same_Partition_Key()
    {
        // Two requests from the SAME connection but DIFFERENT forged X-Forwarded-For values
        // must resolve to the SAME partition key, so they cannot each obtain a fresh budget.
        var a = new DefaultHttpContext();
        a.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        a.Request.Headers["X-Forwarded-For"] = "1.1.1.1";

        var b = new DefaultHttpContext();
        b.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        b.Request.Headers["X-Forwarded-For"] = "2.2.2.2";

        RateLimitingExtensions.ResolveClientIp(a)
            .Should().Be(RateLimitingExtensions.ResolveClientIp(b));
    }
}
