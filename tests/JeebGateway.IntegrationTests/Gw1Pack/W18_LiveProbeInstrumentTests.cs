using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw1Pack;

/// <summary>
/// GW1 TEST PACK — member item <b>W1.8</b>, second file: <b>validation of the LIVE
/// instrument V-2 is asked to read.</b> Run alone with
/// <c>dotnet test --no-build --filter "FullyQualifiedName~Gw1Pack.W18_LiveProbe"</c>.
///
/// <para><b>Why this file exists.</b> V-2's contract is a <c>service</c>-class read off
/// MSI. Its instrument is <c>GET /health/ready</c> — specifically the <c>store-durability</c>
/// entry. Nothing in the repo previously proved that <i>that particular string</i> can
/// discriminate a durable ledger from an in-memory one, and this programme's recorded
/// failure mode is exactly an instrument that reports confidently and wrongly (ten
/// checkers audited in b02, all ten wrong). So the instrument gets its own controls
/// before anybody scores a batch on it.</para>
///
/// <para><b>Correction to the batch document, stated plainly.</b> <c>GW1.md</c>'s V-2
/// section says the health line <i>"only interpolates <c>Critical.Length</c> into its own
/// log line, so 'all 33 critical stores durable' reports an array length, not
/// durability. It is the cheap half of the gate."</i> That is <b>half wrong, in the
/// pessimistic direction</b>. Read <c>StoreDurabilityHealthCheck.CheckHealthAsync</c>:
/// the Healthy branch is reached <i>only after</i>
/// <c>StoreDurabilityGuard.Evaluate(iface =&gt; _services.GetService(iface)?.GetType())</c>
/// returns <b>zero violations</b> — i.e. after every Critical interface has been resolved
/// from the <b>live container</b> and its <b>concrete runtime type</b> matched against the
/// approved durable set. The interpolated number is cosmetic; the <i>condition</i> that
/// emits it is a per-store live type check. Since W1.8 puts
/// <see cref="ISettlementLedgerClient"/> into <c>Critical</c> bound to
/// <see cref="PostgresSettlementLedgerClient"/>, a live
/// <c>store-durability: all 34 critical stores durable</c> IS a live read of that store's
/// resolved concrete type. <b>S1/S2/S4 below prove that; S3 proves its one loophole.</b></para>
///
/// <para><b>Note carefully what the health check does NOT have.</b> Unlike the boot gate
/// (<c>StoreDurabilityGuard.EnsureDurable</c>), it reads <b>no</b>
/// <c>StoreDurability:FailClosedDisabled</c> escape hatch — see S6. The only way to make
/// it Healthy without evaluating anything is to run it in Development/Testing, which is
/// S3's loophole and why the probe must match the DESCRIPTION, never the status.</para>
///
/// <para><b>Still NOT PROVEN by anything here, and no green below is evidence of it:</b>
/// that a ledger ROW is written to and read back from Postgres. That needs SQL execution.
/// Testcontainers needs Docker (banned) and direct <c>psql</c> to the datastore box
/// <c>192.168.2.20</c> is forbidden by owner rule. S5 identifies the one
/// <b>sanctioned</b> signal that does prove it — a journal line only the durable client
/// can emit — and makes that grep attributable.</para>
/// </summary>
public class W18_LiveProbeInstrumentTests
{
    private sealed class FakeEnv : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production";
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    private sealed class MapServiceProvider : IServiceProvider
    {
        private readonly IReadOnlyDictionary<Type, object> _map;
        public MapServiceProvider(IReadOnlyDictionary<Type, object> map) => _map = map;
        public object? GetService(Type serviceType) => _map.TryGetValue(serviceType, out var v) ? v : null;
    }

    /// <summary>
    /// Every Critical interface resolved to its first approved durable concrete type.
    /// Instances are created WITHOUT running a constructor, so no DB/Redis/upstream is
    /// needed — the guard only ever inspects <c>GetType()</c>. Same technique the
    /// pre-existing <c>StoreDurabilityFailClosedTests</c> uses.
    /// </summary>
    private static Dictionary<Type, object> AllDurableMap()
    {
        var map = new Dictionary<Type, object>();
        foreach (var (iface, durable) in StoreDurabilityGuard.Critical)
            map[iface] = RuntimeHelpers.GetUninitializedObject(durable[0]);
        return map;
    }

    /// <summary>The exact literal a verifier greps for in MSI's <c>/health/ready</c> payload.</summary>
    private const string LiveHealthyDescription = "store-durability: all 34 critical stores durable";

    // ── S1 — the live string is REACHABLE, and it is byte-exact ────────────

    [Fact]
    public async Task S1_An_All_Durable_ProdLike_Container_Emits_The_Exact_String_MSI_Reports()
    {
        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(AllDurableMap()), new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);

        // POSITIVE CONTROL for V-2's grep. If MSI's payload and this literal ever differ,
        // the verifier's string match is measuring nothing and would silently pass on a
        // renamed check. Byte-exact, not Contain().
        result.Description.Should().Be(LiveHealthyDescription,
            "this is the literal a service-class verifier matches in MSI's /health/ready payload; " +
            "if the format moves, the live probe must move with it");

        // …and the 34 in that literal is the SEALED predicate, not a free number.
        // RE-SEALED 33 -> 34 at W1-02 (G-08): IStateOwnershipClient joined Critical in this PR.
        StoreDurabilityGuard.Critical.Length.Should().Be(34,
            "SEALED-PREDICATES.md §4 / OWNER-DECISIONS.md 2026-07-31 'PROMOTE', re-sealed at W1-02");
    }

    // ── S2 — the string is DISCRIMINATING for this batch's store ───────────

    [Fact]
    public async Task S2_Swapping_Only_The_Settlement_Ledger_To_InMemory_Destroys_That_String()
    {
        // The whole question V-2's row asks: does the live probe actually see THIS store?
        // Everything else stays durable, so a red here is attributable to the ledger alone.
        var map = AllDurableMap();
        map[typeof(ISettlementLedgerClient)] =
            RuntimeHelpers.GetUninitializedObject(typeof(InMemorySettlementLedgerClient));

        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(map), new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "a prod-like gateway serving its cash ledger from process memory must not report ready");
        result.Description.Should().NotBe(LiveHealthyDescription);
        result.Description.Should().Contain("ISettlementLedgerClient",
            "an unnamed red is not attributable to W1.8");
        result.Description.Should().Contain("InMemorySettlementLedgerClient",
            "and it must say what it resolved to instead");
    }

    // ── S3 — THE LOOPHOLE. Status alone is worthless. ──────────────────────

    [Fact]
    public async Task S3_A_Development_Host_Also_Reports_Healthy_So_Status_Alone_Is_Not_Evidence()
    {
        // A verifier who reads `"status":"Healthy"` off store-durability and stops has
        // proven NOTHING: the same Healthy is returned, with every critical store in
        // memory, whenever the process is in Development or Testing. The pre-existing
        // suite asserts only the status here and would not notice.
        var map = AllDurableMap();
        map[typeof(ISettlementLedgerClient)] =
            RuntimeHelpers.GetUninitializedObject(typeof(InMemorySettlementLedgerClient));

        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(map), new FakeEnv { EnvironmentName = Environments.Development });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy, "the guard is a documented no-op in Development");
        result.Description.Should().Be("store-durability: exempt (Development/Testing)");
        result.Description.Should().NotBe(LiveHealthyDescription,
            "THE RULE FOR V-2: match the DESCRIPTION, never the status. An exempt host is " +
            "indistinguishable from a fully durable one on status alone");
    }

    // ── S4 — the count in the string cannot be faked by an empty evaluation ─

    [Fact]
    public async Task S4_A_Container_That_Resolves_Nothing_Reds_Rather_Than_Reporting_All_Durable()
    {
        // Guards against the vacuous-green shape: a provider that returns null for every
        // interface must NOT read as "all durable". If it did, a probe pointed at a
        // half-built container would report the batch green.
        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(new Dictionary<Type, object>()), new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("ISettlementLedgerClient");
    }

    // ── S5 — the one SANCTIONED signal that a row really was written ───────

    [Fact]
    public void S5_The_Ledger_Posted_Journal_Line_Can_Only_Have_Come_From_The_Durable_Client()
    {
        // WHY THIS MATTERS. The row-level claim (a ledger entry is written to Postgres and
        // read back) cannot be checked with psql: the owner rule is "never connect directly
        // to 192.168.2.20". The sanctioned substitute is the gateway's OWN journal on MSI.
        // PostgresSettlementLedgerClient logs at Information ONLY AFTER
        // ExecuteReaderAsync + ReadAsync have succeeded — i.e. only after the INSERT ran
        // against the DEPLOYED schema and returned a row. So the line's presence proves
        // migration 0044 is applied and the SQL is valid; its absence alongside
        // SettlementService's swallowed-exception warning is the silent-failure case.
        //
        // That inference is only sound if the line is UNIQUE to the durable client. Assert
        // it, rather than assuming it.
        var root = RepoRoot();
        var postgres = File.ReadAllText(Path.Combine(root, "src", "JeebGateway", "Financials", "PostgresSettlementLedgerClient.cs"));
        var inMemory = File.ReadAllText(Path.Combine(root, "src", "JeebGateway", "Financials", "InMemorySettlementLedgerClient.cs"));

        const string template = "Settlement ledger entry posted idempotencyKey={IdempotencyKey}";

        postgres.Should().Contain(template,
            "the durable client must emit an attributable line; without it the journal grep has no subject");
        inMemory.Should().NotContain(template,
            "if BOTH clients logged this, a journal hit would not distinguish durable from in-memory " +
            "and the whole service-class inference collapses");

        // Structural corroboration of the same fact, independent of the string: the
        // in-memory client has no logger at all, so it CANNOT emit anything.
        typeof(InMemorySettlementLedgerClient).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(t => typeof(ILogger).IsAssignableFrom(t));

        // POSITIVE CONTROL for the line above — the same predicate must be able to find a
        // logger when one is there, or "no logger" is an artefact of the predicate.
        typeof(PostgresSettlementLedgerClient).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType)
            .Should().Contain(t => typeof(ILogger).IsAssignableFrom(t));
    }

    // ── S6 — the escape hatch does NOT reach the readiness probe ───────────

    [Fact]
    public async Task S6_The_FailClosedDisabled_Escape_Hatch_Cannot_Silence_The_Readiness_Probe()
    {
        // The boot gate has a documented test-harness escape hatch. If the readiness check
        // honoured it too, a stray StoreDurability__FailClosedDisabled=true in MSI's
        // gateway.env would make /health/ready green while money sat in a dictionary — and
        // V-2's entire service-class row would be worthless. It does not honour it: the
        // check never reads IConfiguration. Proven by giving it a provider that WOULD
        // return `true` for the flag and watching it red anyway.
        var map = AllDurableMap();
        map[typeof(ISettlementLedgerClient)] =
            RuntimeHelpers.GetUninitializedObject(typeof(InMemorySettlementLedgerClient));
        map[typeof(Microsoft.Extensions.Configuration.IConfiguration)] =
            new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [StoreDurabilityGuard.FailClosedDisabledKey] = "true",
                }).Build();

        // POSITIVE CONTROL — the flag really would disable the BOOT gate, so the NEG below
        // is about the readiness probe specifically and not about an inert flag value.
        var provider = new MapServiceProvider(map);
        StoreDurabilityGuard.IsFailClosedDisabled(provider).Should().BeTrue(
            "the flag must be genuinely readable and genuinely true for this control to mean anything");

        var result = await new StoreDurabilityHealthCheck(provider, new FakeEnv { EnvironmentName = "Production" })
            .CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "the readiness probe has no escape hatch — that is what makes it a usable live instrument");
        result.Description.Should().Contain("ISettlementLedgerClient");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the pack must be able to locate the repo root from the test binary");
        return dir!.FullName;
    }
}
