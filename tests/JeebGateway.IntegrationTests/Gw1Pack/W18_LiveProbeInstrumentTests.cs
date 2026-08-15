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
    private const string LiveHealthyDescription = "store-durability: all 30 critical stores durable";

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

        // …and the 30 in that literal is the SEALED predicate, not a free number.
        // RE-SEALED 33 -> 34 at W1-02, 34 -> 30 at W2-R02 (G-08): the four settlement entries left.
        StoreDurabilityGuard.Critical.Length.Should().Be(30,
            "SEALED-PREDICATES.md §4 / OWNER-DECISIONS.md 2026-07-31 'PROMOTE', re-sealed at W2-R02");
    }

    // ── S2 — the string is DISCRIMINATING for this batch's store ───────────

    // RE-TARGETED at gwdbx W2-R02: ISettlementLedgerClient left the Critical roster with its table
    // (0052), so the probe can no longer see it. The claim — "the probe names the ONE store that
    // degraded" — is re-proven on a store that is still rostered.
    [Fact]
    public async Task S2_Swapping_Only_The_Refresh_Token_Store_To_InMemory_Destroys_That_String()
    {
        // The whole question V-2's row asks: does the live probe actually see a degraded store?
        // Everything else stays durable, so a red here is attributable to that store alone.
        var map = AllDurableMap();
        map[typeof(JeebGateway.Tokens.IRefreshTokenStore)] =
            RuntimeHelpers.GetUninitializedObject(typeof(JeebGateway.Tokens.InMemoryRefreshTokenStore));

        var check = new StoreDurabilityHealthCheck(
            new MapServiceProvider(map), new FakeEnv { EnvironmentName = "Production" });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy,
            "a prod-like gateway serving a critical store from process memory must not report ready");
        result.Description.Should().NotBe(LiveHealthyDescription);
        result.Description.Should().Contain("IRefreshTokenStore",
            "an unnamed red is not attributable to a store");
        result.Description.Should().Contain("InMemoryRefreshTokenStore",
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
        map[typeof(JeebGateway.Tokens.IRefreshTokenStore)] =
            RuntimeHelpers.GetUninitializedObject(typeof(JeebGateway.Tokens.InMemoryRefreshTokenStore));

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
        // RE-TARGETED at W2-R02: the settlement ledger left the roster, so name a store still on it.
        result.Description.Should().Contain("IRefreshTokenStore");
    }

    // ── S5 — REMOVED at gwdbx W2-R11 ──────────────────────────────────────
    // It sealed the "Settlement ledger entry posted" journal line as unique to the durable ledger
    // client. Both ledger clients are deleted, so there is no subject left to seal.

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
        map[typeof(JeebGateway.Tokens.IRefreshTokenStore)] =
            RuntimeHelpers.GetUninitializedObject(typeof(JeebGateway.Tokens.InMemoryRefreshTokenStore));
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
        result.Description.Should().Contain("IRefreshTokenStore");
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
