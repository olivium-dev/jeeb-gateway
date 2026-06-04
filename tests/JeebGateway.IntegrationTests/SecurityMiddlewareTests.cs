using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-032 — edge security middleware acceptance tests.
/// Covers: rate limiting (429 + Retry-After), security headers, CORS preflight,
/// auth-route anonymity.
/// </summary>
public class SecurityMiddlewareTests
{
    [Fact]
    public async Task Responses_Carry_Owasp_Security_Headers()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.Should().ContainKey("Strict-Transport-Security");
        resp.Headers.GetValues("Strict-Transport-Security").Single()
            .Should().Contain("max-age=").And.Contain("includeSubDomains");
        resp.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        resp.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        resp.Headers.GetValues("Referrer-Policy").Single().Should().Be("no-referrer");
        resp.Headers.Should().ContainKey("Permissions-Policy");
        resp.Headers.Should().ContainKey("Content-Security-Policy");
        resp.Headers.Should().ContainKey("Cross-Origin-Opener-Policy");
        resp.Headers.Should().NotContainKey("X-Powered-By");
    }

    [Fact]
    public async Task Cors_Preflight_From_Admin_Origin_Is_Allowed()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        req.Headers.Add("Origin", "http://localhost:5173");
        req.Headers.Add("Access-Control-Request-Method", "GET");
        req.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        resp.Headers.GetValues("Access-Control-Allow-Origin").Single()
            .Should().Be("http://localhost:5173");
        resp.Headers.GetValues("Access-Control-Allow-Credentials").Single()
            .Should().Be("true");
        resp.Headers.GetValues("Access-Control-Allow-Methods").Single()
            .Should().Contain("GET");
    }

    [Fact]
    public async Task Cors_Preflight_From_Disallowed_Origin_Is_Refused()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        using var req = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        req.Headers.Add("Origin", "https://evil.example.com");
        req.Headers.Add("Access-Control-Request-Method", "GET");

        var resp = await client.SendAsync(req);

        // CORS middleware does not emit ACAO for disallowed origins, which is
        // what causes the browser to block the response. The status itself may
        // still be 204 — the absence of the header is the deny signal.
        resp.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Auth_Routes_Are_Anonymous_And_Not_JwtGated()
    {
        // F3: the route is still anonymous at the JWT-middleware layer — the
        // mint's privileged-caller gate is an in-controller check, not the
        // global JWT bearer policy. Disable the mint gate here so this test
        // continues to assert the *middleware* anonymity property (reaches the
        // controller and returns 400 for an invalid body, not a 401 from JWT
        // auth). The gate itself is covered by TokensEndpointTests.
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:TokenMint:Enabled"] = "false"
                    });
                });
            });
        var client = factory.CreateClient();

        // POST /auth/tokens with invalid body returns 400 (BadRequest), not
        // 401: anonymous access is allowed for token issuance.
        var resp = await client.PostAsJsonAsync<object?>("/auth/tokens", null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Rate_Limit_Returns_429_With_Retry_After_When_Per_User_Quota_Exhausted()
    {
        const int budget = 5;

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:RateLimit:Enabled"] = "true",
                        ["Security:RateLimit:UserPermitsPerMinute"] = budget.ToString(),
                        ["Security:RateLimit:IpPermitsPerMinute"] = "10000",
                        ["Security:RateLimit:WindowSegments"] = "1"
                    });
                });
            });
        var client = factory.CreateClient();
        var userId = $"rl-user-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < budget + 2; i++)
        {
            var r = await client.GetAsync("/api/health");
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = r;
                break;
            }
        }

        rejected.Should().NotBeNull("the per-user budget must reject once exhausted");
        rejected!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.Should().ContainKey("Retry-After");
        rejected.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var body = await rejected.Content.ReadAsStringAsync();
        body.Should().Contain("Too Many Requests");
    }

    [Fact]
    public async Task Rate_Limit_Returns_429_With_Retry_After_When_Per_Ip_Quota_Exhausted()
    {
        const int ipBudget = 4;

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:RateLimit:Enabled"] = "true",
                        ["Security:RateLimit:UserPermitsPerMinute"] = "10000",
                        ["Security:RateLimit:IpPermitsPerMinute"] = ipBudget.ToString(),
                        ["Security:RateLimit:WindowSegments"] = "1"
                    });
                });
            });
        var client = factory.CreateClient();
        // No X-User-Id — exercise the IP partition for anonymous traffic.

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < ipBudget + 3; i++)
        {
            var r = await client.GetAsync("/api/health");
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = r;
                break;
            }
        }

        rejected.Should().NotBeNull("the per-IP budget must reject once exhausted");
        rejected!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.Should().ContainKey("Retry-After");
    }

    [Fact]
    public async Task Health_Probes_Pass_Through_Security_Pipeline()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        var ready = await client.GetAsync("/health/ready");

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
        live.Headers.Should().ContainKey("X-Content-Type-Options");
    }
}
