using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC2 — aggregated health endpoint.
///
/// NOTE: the aggregated surface moved from /health to /health/aggregate. /health
/// is now a LIVENESS alias (process-only, never gates on downstreams) to fix the
/// production incident where a flapping downstream 503'd the only PROD gateway
/// replica via the swarm liveness probe. The full red/green dashboard view lives
/// at /health/aggregate; /health/ready remains the readiness surface.
///
/// Asserts:
///   * /health/aggregate returns 200 with status=Healthy when every check passes
///   * /health/aggregate returns 503 when any check fails, with the failing
///     service named in the JSON body's "failing" array AND the per-check entries
///   * /health/live stays 200 even while downstream checks are unhealthy
///   * /health/ready returns 503 with the failing downstream named
/// </summary>
public class AggregateHealthEndpointTests
{
    [Fact]
    public async Task HealthAggregate_Is_200_When_All_Checks_Healthy()
    {
        using var factory = NewFactory(injectFailingCheck: false);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/aggregate");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("Healthy");
        json.GetProperty("failing").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Health_Liveness_Stays_200_When_Downstream_Unhealthy()
    {
        // The incident-fix contract: /health is liveness-only and must return
        // 200 even when an injected downstream check is Unhealthy.
        using var factory = NewFactory(injectFailingCheck: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthAggregate_Is_503_And_Names_Failing_Downstream()
    {
        using var factory = NewFactory(injectFailingCheck: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/aggregate");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var body = await resp.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("status").GetString().Should().Be("Unhealthy");

        var failing = json.GetProperty("failing");
        failing.EnumerateArray().Select(e => e.GetString()).Should().Contain("simulated-delivery-service");

        // Per-check detail also names the failing service.
        var checks = json.GetProperty("checks");
        var failed = checks.EnumerateArray()
            .Single(c => c.GetProperty("name").GetString() == "simulated-delivery-service");
        failed.GetProperty("status").GetString().Should().Be("Unhealthy");
    }

    [Fact]
    public async Task HealthLive_Stays_200_When_Downstream_Unhealthy()
    {
        using var factory = NewFactory(injectFailingCheck: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/live");

        // K8s liveness must not flap on downstream issues; only the "self"
        // check (always Healthy here) participates.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task HealthReady_Is_503_When_Downstream_Unhealthy_And_Names_Service()
    {
        using var factory = NewFactory(injectFailingCheck: true);
        using var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/ready");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("simulated-delivery-service");
    }

    private static WebApplicationFactory<Program> NewFactory(bool injectFailingCheck)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Force Testing env so the BffStartupValidator skips required-URL
                // validation (covered separately in BffStartupValidatorTests).
                builder.UseEnvironment("Testing");

                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        // JeebJwt — HS512 (≥64 bytes) and the phone pepper are
                        // validated at startup; mirror OtpServiceWebAppFactory.
                        ["JeebJwt:SigningKey"]  = "test-signing-key-must-be-at-least-sixty-four-bytes-for-HS512-padding!!",
                        ["JeebJwt:PhonePepper"] = "test-phone-pepper-must-be-at-least-thirty-two-bytes-for-HMAC-SHA256",
                        // ServiceAuth — required by AddOptions<>().ValidateOnStart()
                        // even though the handler is not exercised here.
                        ["ServiceAuth:SigningKey"] = "testing-signing-key-32-chars-or-longer-aaaa",
                    });
                });

                if (injectFailingCheck)
                {
                    builder.ConfigureTestServices(services =>
                    {
                        services.AddHealthChecks()
                            .AddCheck(
                                "simulated-delivery-service",
                                () => HealthCheckResult.Unhealthy("simulated 503"),
                                tags: new[] { "ready", "downstream" });
                    });
                }
            });
    }
}
