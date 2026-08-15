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
/// Settlement authority wiring regression. Wallet-service must be the active writer regardless
/// of whether the gateway's legacy Postgres projection is configured. The legacy Postgres and
/// in-memory clients may remain as migration/read fixtures, but neither may satisfy the production
/// durability boundary.
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
    public void SettlementLedger_Resolves_To_Wallet_When_GatewayPostgres_Configured()
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
            .Should().BeOfType<WalletSettlementLedgerClient>(
                "GatewayPostgres is only a temporary projection/shadow; it cannot own money writes");
    }

    [Fact]
    public void SettlementLedger_Resolves_To_Wallet_When_GatewayPostgres_Absent()
    {
        // Gateway Postgres is not a selector for financial authority. Local/test uses the same
        // wallet boundary and the configured localhost wallet URL; no in-memory writer fallback.
        using var factory = new WebApplicationFactory<Program>();

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<WalletSettlementLedgerClient>();
    }

    [Fact]
    public void Settlement_Shadow_Flag_Wraps_Wallet_Primary_Without_Replacing_It()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("GatewayPostgres:ConnectionString", FakeCs);
                builder.UseSetting("WalletLedgerMigration:SettlementShadowCompareEnabled", "true");
            });

        factory.Services.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<ShadowComparingSettlementLedgerClient>();
        factory.Services.GetRequiredService<WalletSettlementLedgerClient>()
            .Should().NotBeNull("the comparator decorates, rather than replaces, wallet authority");
    }

    [Fact]
    public void Settlement_Shadow_Flag_Without_Legacy_Dsn_Fails_Closed()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("WalletLedgerMigration:SettlementShadowCompareEnabled", "true"));

        var act = () => factory.Services.GetRequiredService<ISettlementLedgerClient>();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*SettlementShadowCompareEnabled requires GatewayPostgres:ConnectionString*");
    }

    // ── Durability guard promotion (owner ruling: PROMOTE) ─────────────────

    [Fact]
    public void SettlementLedger_Is_A_Critical_Boundary_Requiring_The_Wallet_Client()
    {
        var critical = StoreDurabilityGuard.Critical
            .FirstOrDefault(c => c.Iface == typeof(ISettlementLedgerClient));

        critical.Iface.Should().Be(typeof(ISettlementLedgerClient),
            "the cash-settlement ledger carries money movement, so it belongs in the Critical fail-closed set");
        critical.DurableImpls.Should().Contain(typeof(WalletSettlementLedgerClient))
            .And.Contain(typeof(ShadowComparingSettlementLedgerClient),
                "the optional wrapper still returns only the authoritative wallet result");
        critical.DurableImpls.Should().NotContain(typeof(InMemorySettlementLedgerClient),
            "an in-process writer loses the idempotency ledger on restart");
        critical.DurableImpls.Should().NotContain(typeof(PostgresSettlementLedgerClient),
            "the gateway database is a legacy comparison source, not financial authority");
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
    public void Critical_Holds_Exactly_26_Gateway_Owned_Stores()
    {
        // Sealed gate value (SEALED-PREDICATES.md §4, row GW1-1): 32 at origin/main 24b3dd6,
        // 26 after the owner-service and GDPR state-work cutovers. This is a TRIPWIRE, not the claim — Critical.Length is only
        // interpolated into a health-check log line, so "27 critical stores durable" reports an
        // ARRAY LENGTH, not durability. The load-bearing assertions are the two above (the
        // interface is present and bound to the durable type) and the fail-closed test below.
        // If a later batch legitimately changes the ownership set, update this number deliberately and
        // re-seal — do not delete the assertion.
        StoreDurabilityGuard.Critical.Should().HaveCount(26);
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
    public void EnsureDurable_ProdLike_With_Wallet_SettlementLedger_Boots()
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
        result.Description.Should().Be("store-durability: all 26 critical stores durable");
    }
}
