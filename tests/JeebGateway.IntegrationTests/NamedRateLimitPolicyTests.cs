using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-032 — named rate-limit policy tests.
/// Covers: auth token bucket policy, sensitive fixed window policy.
/// </summary>
public class NamedRateLimitPolicyTests
{
    [Fact]
    public async Task Auth_Token_Bucket_Returns_429_After_Burst_Exhausted()
    {
        const int bucketLimit = 3;

        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:RateLimit:Enabled"] = "true",
                        ["Security:RateLimit:AuthTokenBucketLimit"] = bucketLimit.ToString(),
                        ["Security:RateLimit:AuthTokensPerPeriod"] = "1",
                        ["Security:RateLimit:AuthReplenishmentSeconds"] = "300",
                        ["Security:RateLimit:UserPermitsPerMinute"] = "10000",
                        ["Security:RateLimit:IpPermitsPerMinute"] = "10000",
                        ["Security:RateLimit:WindowSegments"] = "1"
                    });
                });
            });
        var client = factory.CreateClient();

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < bucketLimit + 3; i++)
        {
            var content = new System.Net.Http.StringContent(
                """{"userId":"test-user"}""",
                System.Text.Encoding.UTF8,
                "application/json");
            var r = await client.PostAsync("/auth/tokens", content);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = r;
                break;
            }
        }

        rejected.Should().NotBeNull("the token bucket must reject after burst exhaustion");
        rejected!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        rejected.Headers.Should().ContainKey("Retry-After");
    }

    [Fact]
    public async Task Rate_Limit_Disabled_Allows_All_Auth_Requests()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:RateLimit:Enabled"] = "false"
                    });
                });
            });
        var client = factory.CreateClient();

        HttpResponseMessage? rejected = null;
        for (var i = 0; i < 10; i++)
        {
            var content = new System.Net.Http.StringContent(
                """{"userId":"test-user"}""",
                System.Text.Encoding.UTF8,
                "application/json");
            var r = await client.PostAsync("/auth/tokens", content);
            if (r.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rejected = r;
                break;
            }
        }

        rejected.Should().BeNull("rate limiting is disabled — no 429 expected");
    }

    [Fact]
    public async Task Per_User_Sliding_Window_Isolates_Different_Users()
    {
        const int budget = 3;

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

        var user1 = $"user-{Guid.NewGuid()}";
        var user2 = $"user-{Guid.NewGuid()}";

        // Exhaust user1's budget.
        for (var i = 0; i < budget + 1; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
            req.Headers.Add("X-User-Id", user1);
            await client.SendAsync(req);
        }

        // User2 should still have their full budget.
        using var user2Req = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        user2Req.Headers.Add("X-User-Id", user2);
        var resp = await client.SendAsync(user2Req);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
