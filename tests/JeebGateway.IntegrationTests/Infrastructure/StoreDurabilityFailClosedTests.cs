using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using FluentAssertions;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// AUDIT-A (FIX-1) — the gateway must refuse to boot in a prod-like environment when any critical
/// store of record silently fell back to in-memory (a dropped durability selector env var). Store
/// selection is a spread of silent <c>if(set){durable}else{inMemory}</c> branches with no exception
/// and a green health check, so a single missing env var could serve money/identity/audit/legal
/// state from process memory. These tests prove: prod-like + an in-memory critical store → startup
/// fails naming the store; all-durable → ok; Development/Testing → no-op (in-memory kept for local
/// and the test harness); and the readiness health check mirrors the gate.
/// </summary>
public class StoreDurabilityFailClosedTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    /// <summary>IServiceProvider backed by a fixed interface→instance map; unknown types resolve null.</summary>
    private sealed class MapServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _map;
        public MapServiceProvider(IReadOnlyDictionary<Type, object> map) => _map = map;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    /// <summary>
    /// A service provider where every critical interface resolves to its (first) approved durable
    /// concrete type — instantiated WITHOUT running its constructor, so we need no DB/Redis/upstream.
    /// The guard only inspects <c>GetType()</c>, so an uninitialised instance is sufficient.
    /// </summary>
    private static Dictionary<Type, object> AllDurableMap()
    {
        var map = new Dictionary<Type, object>();
        foreach (var (iface, durable) in StoreDurabilityGuard.Critical)
        {
            map[iface] = RuntimeHelpers.GetUninitializedObject(durable[0]);
        }
        return map;
    }

    private static IServiceProvider AllDurableProvider() => new MapServiceProvider(AllDurableMap());

    private static IServiceProvider ProviderWith(Type iface, Type concrete)
    {
        var map = AllDurableMap();
        map[iface] = RuntimeHelpers.GetUninitializedObject(concrete);
        return new MapServiceProvider(map);
    }

    private static IServiceProvider ProviderMissing(Type iface)
    {
        var map = AllDurableMap();
        map.Remove(iface);
        return new MapServiceProvider(map);
    }

    // ---- Evaluate core (pure decision) ----------------------------------------------------------

    [Fact]
    public void Evaluate_AllDurable_Yields_No_Violations()
    {
        var provider = AllDurableProvider();
        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());
        violations.Should().BeEmpty("every critical interface resolved to an approved durable type");
    }

    [Fact]
    public void Evaluate_InMemory_Critical_Store_Is_A_Violation_Naming_The_Store()
    {
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));

        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());

        violations.Should().ContainSingle();
        violations[0].Should().Contain("ISettlementStore").And.Contain("InMemorySettlementStore");
    }

    [Fact]
    public void Evaluate_Unregistered_Critical_Store_Is_A_Violation()
    {
        var provider = ProviderMissing(typeof(JeebGateway.Requests.IRequestsStore));
        var violations = StoreDurabilityGuard.Evaluate(t => provider.GetService(t)?.GetType());
        violations.Should().ContainSingle();
        violations[0].Should().Contain("IRequestsStore").And.Contain("null");
    }

    // ---- EnsureDurable boot gate ---------------------------------------------------------------

    [Fact]
    public void EnsureDurable_ProdLike_With_InMemory_Critical_Store_Fails_Closed()
    {
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));

        var act = () => StoreDurabilityGuard.EnsureDurable(
            provider, new FakeEnv { EnvironmentName = "Production" }, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>("a prod-like gateway must refuse to serve in-memory money state")
            .WithMessage("*FAIL-CLOSED*")
            .WithMessage("*ISettlementStore*", "the failure must name the offending store");
    }

    [Fact]
    public void EnsureDurable_ProdLike_All_Durable_Does_Not_Throw()
    {
        var act = () => StoreDurabilityGuard.EnsureDurable(
            AllDurableProvider(), new FakeEnv { EnvironmentName = "Production" }, NullLogger.Instance);

        act.Should().NotThrow("all critical stores resolved to durable implementations");
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public void EnsureDurable_Arms_In_Any_Non_Dev_Non_Testing_Environment(string env)
    {
        var provider = ProviderWith(
            typeof(JeebGateway.Admin.IAdminAuditLog),
            typeof(JeebGateway.Financials.InMemorySettlementStore)); // any non-durable type

        var act = () => StoreDurabilityGuard.EnsureDurable(
            provider, new FakeEnv { EnvironmentName = env }, NullLogger.Instance);

        act.Should().Throw<InvalidOperationException>($"'{env}' is prod-like and the gate must arm");
    }

    [Fact]
    public void EnsureDurable_Is_NoOp_In_Development()
    {
        // Local dev legitimately runs every store in-memory.
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));

        var act = () => StoreDurabilityGuard.EnsureDurable(
            provider, new FakeEnv { EnvironmentName = Environments.Development }, NullLogger.Instance);

        act.Should().NotThrow("Development must keep in-memory stores and never block boot");
    }

    [Fact]
    public void EnsureDurable_Is_NoOp_In_Testing()
    {
        // The integration-test harness legitimately runs in-memory.
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));

        var act = () => StoreDurabilityGuard.EnsureDurable(
            provider, new FakeEnv { EnvironmentName = "Testing" }, NullLogger.Instance);

        act.Should().NotThrow("the test harness must keep in-memory stores and never block boot");
    }

    // ---- Sanity: no store is both Critical and on the in-memory backlog -------------------------

    [Fact]
    public void Backlog_And_Critical_Do_Not_Overlap()
    {
        var criticalIfaces = StoreDurabilityGuard.Critical.Select(c => c.Iface).ToHashSet();
        StoreDurabilityGuard.KnownInMemoryBacklog.Should()
            .OnlyContain(t => !criticalIfaces.Contains(t),
                "a store with a durable target must not also be listed as a known-in-memory exemption");
    }

    // ---- Readiness health check mirrors the gate -----------------------------------------------

    [Fact]
    public async System.Threading.Tasks.Task HealthCheck_ProdLike_InMemory_Reports_Unhealthy()
    {
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));
        var check = new StoreDurabilityHealthCheck(provider, new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("ISettlementStore");
    }

    [Fact]
    public async System.Threading.Tasks.Task HealthCheck_ProdLike_AllDurable_Reports_Healthy()
    {
        var check = new StoreDurabilityHealthCheck(AllDurableProvider(), new FakeEnv { EnvironmentName = "Production" });
        var result = await check.CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async System.Threading.Tasks.Task HealthCheck_Development_Reports_Healthy_Even_With_InMemory()
    {
        var provider = ProviderWith(
            typeof(JeebGateway.Financials.ISettlementStore),
            typeof(JeebGateway.Financials.InMemorySettlementStore));
        var check = new StoreDurabilityHealthCheck(provider, new FakeEnv { EnvironmentName = Environments.Development });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }
}
