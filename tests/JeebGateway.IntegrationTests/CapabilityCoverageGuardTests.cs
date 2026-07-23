using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-005 Layer 2 — the FINAL coverage gate, executed as a real-host test (not grep).
///
/// <para>The <see cref="CapabilityCoverageGuard"/> is an <c>IHostedService</c> that, at startup,
/// reflection-scans the live <c>EndpointDataSource</c> and (when
/// <c>CapabilityGuard:Enforce=true</c>, the production default) THROWS if any controller action
/// carries neither <c>[RequireCapability]</c> nor <c>[PublicEndpoint]</c>. These tests boot the
/// real <see cref="Program"/> host so the endpoint graph is fully materialized, then assert the
/// guard's authoritative verdict.</para>
///
/// <list type="bullet">
/// <item><b>Happy path</b> — the booted host's guard reports ZERO uncovered actions across all
/// ~50 controllers (every action is either capability-annotated or explicitly public). Because
/// <c>Enforce=true</c>, booting the factory at all already proves the hosted guard did not throw;
/// the explicit assertion makes the coverage number visible and names any regression.</item>
/// <item><b>Default-deny fires</b> — adding a deliberately un-annotated fixture controller as an
/// application part makes the SAME guard report it as uncovered, proving the guard is not
/// vacuously green (ADR-005 confirmation gate: "a deliberately un-annotated fixture action proves
/// it fires").</item>
/// <item><b>Enforce default</b> — <see cref="CapabilityGuardOptions.Enforce"/> defaults to
/// <c>true</c> so a regression (new un-annotated action) fails CI/startup, not just logs.</item>
/// </list>
/// </summary>
public sealed class CapabilityCoverageGuardTests
{
    [Fact]
    public async Task Guard_Reports_Zero_Uncovered_Actions_On_The_Real_Host()
    {
        await using var factory = new WebApplicationFactory<Program>();

        // Force the host (EndpointDataSource + the hosted guard) to fully materialize.
        using var _ = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var guard = factory.Services.GetRequiredService<CapabilityCoverageGuard>();
        var uncovered = guard.FindUncoveredActions();

        uncovered.Should().BeEmpty(
            "ADR-005 default-deny: every controller action must declare [RequireCapability] or "
            + "[PublicEndpoint]. Uncovered: {0}", string.Join(", ", uncovered));
    }

    [Fact]
    public void Enforce_Defaults_To_True()
    {
        // The FINAL one-shot step flips enforcement ON by default; CapabilityGuard:Enforce=false is
        // only an emergency operator override. This pins the safe default against accidental revert.
        new CapabilityGuardOptions().Enforce.Should().BeTrue(
            "the default-deny guard must FAIL the build on an un-annotated action by default");
    }

    [Fact]
    public async Task Guard_Detects_A_Deliberately_Unannotated_Action_As_Uncovered()
    {
        // Proves the guard actually fires. We add a fixture controller whose only action carries
        // neither [RequireCapability] nor [PublicEndpoint]. The SAME guard must now flag it.
        // Enforce is forced OFF here so the deliberately-broken host can still boot for inspection.
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        [$"{CapabilityGuardOptions.SectionName}:Enforce"] = "false",
                    }));
                builder.ConfigureTestServices(services =>
                    services.AddControllers()
                        .AddApplicationPart(typeof(UnannotatedFixtureController).Assembly));
            });

        using var _ = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var guard = factory.Services.GetRequiredService<CapabilityCoverageGuard>();
        var uncovered = guard.FindUncoveredActions();

        uncovered.Should().Contain(
            n => n.Contains(nameof(UnannotatedFixtureController.DeliberatelyUncovered)),
            "the fixture action carries neither [RequireCapability] nor [PublicEndpoint], so the "
            + "default-deny guard MUST report it — proving the guard is not vacuously green");
    }
}

/// <summary>
/// Test-only fixture: a controller whose action is intentionally left un-annotated to prove the
/// default-deny guard detects omissions. It is added as an application part only inside the
/// negative test's host (with Enforce=false so that host can still boot), and is never part of
/// the gateway's real production endpoint surface.
/// </summary>
[ApiController]
[Route("__test/unannotated")]
public sealed class UnannotatedFixtureController : ControllerBase
{
    [HttpGet]
    public IActionResult DeliberatelyUncovered() => Ok();
}
