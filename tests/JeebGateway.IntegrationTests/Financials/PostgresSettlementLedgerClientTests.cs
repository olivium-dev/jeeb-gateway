using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// b05/GW1 W1.8 + W3.5(b) — OWNER RULING 2026-07-31 "PROMOTE": the cash-settlement ledger
/// (<see cref="ISettlementLedgerClient"/>) is now durably backed and is a Critical store under
/// <see cref="StoreDurabilityGuard"/>. Mirrors the established
/// <c>PostgresSettlementEnqueueStoreTests</c> shape: DI-resolution smoke plus guard
/// classification, all of which run for real with no live Postgres, because
/// <see cref="PostgresSettlementLedgerClient"/>'s constructor only stores its collaborators
/// (<see cref="INpgsqlConnectionFactory"/> holds the connection string and opens no socket).
///
/// <para><b>What these tests do NOT prove, said plainly.</b> They do not prove a ledger entry
/// survives a process restart. That is a behavioural claim about a real database and it needs a
/// live service plus an actual restart — it is GW1's V-2 leg on MSI, not a host-suite leg.
/// Nothing here is padded with a self-passing placeholder to make the file look complete: an
/// <c>Assert.True(true, "verified elsewhere")</c> is a green that means nothing, and this batch
/// exists partly because greens that mean nothing have shipped before.</para>
/// </summary>
public class PostgresSettlementLedgerClientTests
{
    private const string FakeCs =
        "Host=127.0.0.1;Port=1;Database=jeeb_test;Username=jeeb;Password=jeeb;Timeout=1";

    /// <summary>IServiceProvider backed by a fixed interface→instance map; unknown types resolve null.</summary>
    private sealed class MapServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _map;
        public MapServiceProvider(IReadOnlyDictionary<Type, object> map) => _map = map;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private static Dictionary<Type, object> AllDurableMap()
    {
        var map = new Dictionary<Type, object>();
        foreach (var (iface, durable) in StoreDurabilityGuard.Critical)
            map[iface] = RuntimeHelpers.GetUninitializedObject(durable[0]);
        return map;
    }

    // ── DI wiring (real, runs without Postgres) ────────────────────────────

    [Fact]
    public void SettlementLedger_Resolves_To_Postgres_When_GatewayPostgres_Configured()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                // UseSetting lands in host configuration, read BEFORE the Program.cs top-level
                // `gatewayPostgresCs` read. Mirrors PostgresSettlementEnqueueStoreTests.
                b.UseSetting("GatewayPostgres:ConnectionString", FakeCs);
                b.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["GatewayPostgres:ConnectionString"] = FakeCs
                    }));
            });

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<PostgresSettlementLedgerClient>(
                "GatewayPostgres:ConnectionString is configured, so the durable ledger must be selected");
    }

    [Fact]
    public void SettlementLedger_Resolves_To_InMemory_When_GatewayPostgres_Absent()
    {
        // Default test config carries no GatewayPostgres:ConnectionString, so the in-memory
        // fallback must remain the live path for local/CI runs (the fail-closed guard is a
        // no-op in Development/Testing). This is also the POSITIVE CONTROL for the test above:
        // without it, a resolution that ALWAYS returned Postgres would look like a pass.
        using var factory = new WebApplicationFactory<Program>();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<InMemorySettlementLedgerClient>(
                "no connection string is configured, so local/CI runs must keep the in-memory fallback");
    }

    // ── Durability guard promotion (owner ruling: PROMOTE) ─────────────────

    [Fact]
    public void SettlementLedger_Is_A_Critical_Durable_Store_Requiring_PostgresSettlementLedgerClient()
    {
        var critical = StoreDurabilityGuard.Critical
            .FirstOrDefault(c => c.Iface == typeof(ISettlementLedgerClient));

        critical.Iface.Should().Be(typeof(ISettlementLedgerClient),
            "the cash-settlement ledger carries money movement, so it belongs in the Critical fail-closed set");
        critical.DurableImpls.Should().Contain(typeof(PostgresSettlementLedgerClient),
            "the only durable implementation that satisfies the prod-like gate is PostgresSettlementLedgerClient");
        critical.DurableImpls.Should().NotContain(typeof(InMemorySettlementLedgerClient),
            "listing the in-memory client as durable would make the guard assert nothing");
    }

    [Fact]
    public void SettlementLedger_Is_Not_On_IntentionalInMemory_Or_The_Backlog()
    {
        // Under the owner's PROMOTE ruling, registering this interface as an intentional
        // in-memory exemption is an AUTOMATIC FAIL, not an alternative outcome.
        StoreDurabilityGuard.IntentionalInMemory.Should()
            .NotContain(typeof(ISettlementLedgerClient),
                "the settlement ledger is a book of record, not a rebuildable cache");
        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .NotContain(typeof(ISettlementLedgerClient),
                "a store with a durable target must not also be a known-in-memory exemption");
    }

    [Fact]
    public void Critical_Holds_Exactly_33_Stores_After_The_Promotion()
    {
        // Sealed gate value (SEALED-PREDICATES.md §4, row GW1-1): 32 at origin/main 24b3dd6,
        // 33 after this promotion. This is a TRIPWIRE, not the claim — Critical.Length is only
        // interpolated into a health-check log line, so "33 critical stores durable" reports an
        // ARRAY LENGTH, not durability. The load-bearing assertions are the two above (the
        // interface is present and bound to the durable type) and the fail-closed test below.
        // If a later batch legitimately adds a 34th store, update this number deliberately and
        // re-seal — do not delete the assertion.
        StoreDurabilityGuard.Critical.Should().HaveCount(33);
    }

    // ── The promotion is LIVE, not decorative ──────────────────────────────

    [Fact]
    public void Evaluate_ProdLike_With_InMemory_SettlementLedger_Is_A_Violation_Naming_The_Store()
    {
        var map = AllDurableMap();
        map[typeof(ISettlementLedgerClient)] =
            RuntimeHelpers.GetUninitializedObject(typeof(InMemorySettlementLedgerClient));
        var provider = new MapServiceProvider(map);

        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());

        violations.Should().ContainSingle()
            .Which.Should().Contain("ISettlementLedgerClient").And.Contain("InMemorySettlementLedgerClient");
    }

    [Fact]
    public void EnsureDurable_ProdLike_With_InMemory_SettlementLedger_Refuses_To_Boot()
    {
        var map = AllDurableMap();
        map[typeof(ISettlementLedgerClient)] =
            RuntimeHelpers.GetUninitializedObject(typeof(InMemorySettlementLedgerClient));

        var act = () => StoreDurabilityGuard.EnsureDurable(
            new MapServiceProvider(map), new FakeEnv { EnvironmentName = "Production" }, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>(
                "a prod-like gateway must refuse to serve its money ledger out of process memory")
            .WithMessage("*FAIL-CLOSED*")
            .WithMessage("*ISettlementLedgerClient*", "the failure must name the offending store");
    }

    [Fact]
    public void EnsureDurable_ProdLike_With_Postgres_SettlementLedger_Boots()
    {
        // POSITIVE CONTROL for the two tests above: the gate must be able to go GREEN, or a
        // refuse-everything guard would be indistinguishable from a working one.
        var act = () => StoreDurabilityGuard.EnsureDurable(
            new MapServiceProvider(AllDurableMap()),
            new FakeEnv { EnvironmentName = "Production" },
            NullLogger.Instance);

        act.Should().NotThrow("every critical store, including the ledger, resolved to a durable type");
    }

    [Fact]
    public async System.Threading.Tasks.Task HealthCheck_Reports_The_Promoted_Store_Count()
    {
        // Exercises the live readiness line StoreDurabilityGuard.cs interpolates
        // ("store-durability: all N critical stores durable") that V-2 reads off MSI.
        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(AllDurableMap()), new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().Be("store-durability: all 33 critical stores durable");
    }
}
