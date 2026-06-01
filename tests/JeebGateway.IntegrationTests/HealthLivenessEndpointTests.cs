using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Regression guard for the production incident this PR fixes: the deployed
/// gateway was returning HTTP 503 on BOTH <c>/health</c> (liveness) and
/// <c>/health/ready</c> because the <c>/health</c> alias was wired with
/// <c>Predicate = _ => true</c> — i.e. it ran the downstream URL-group probes.
/// Under a health-gated swarm deploy that pulls the only PRODUCTION gateway
/// replica out of rotation whenever any single upstream flaps.
///
/// Liveness MUST be process-only. In <see cref="WebApplicationFactory{TEntryPoint}"/>
/// NO downstream is reachable (the test host binds no real upstream URLs / they
/// resolve to localhost dev ports that are closed), so if liveness depended on
/// downstreams these calls would 503. Asserting 200 proves liveness is
/// downstream-independent.
///
/// Happy path:  /health and /health/live return 200 with zero reachable upstreams.
/// Negative guard: /health is NOT the aggregated surface — a closed downstream
/// must never flip liveness to 503. (/health/ready is the aggregated surface and
/// is exercised by the per-service contract tests.)
/// </summary>
public class HealthLivenessEndpointTests
{
    [Fact]
    public async Task Health_Liveness_Returns_200_With_No_Reachable_Downstreams()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "/health is a liveness alias and must return 200 on process liveness "
            + "alone, never gating on downstream readiness");
    }

    [Fact]
    public async Task Health_Live_Returns_200_With_No_Reachable_Downstreams()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health/live");

        resp.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "/health/live is the dedicated liveness probe and must never depend "
            + "on downstream services");
    }

    [Fact]
    public async Task Health_Liveness_Does_Not_Return_503_When_Downstreams_Unreachable()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().NotBe(
            HttpStatusCode.ServiceUnavailable,
            "a closed/unreachable downstream must never turn the liveness probe red");
    }
}
