using System.Text;
using FluentAssertions;
using JeebGateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Payments;

/// <summary>
/// Standing regression gate for the owner ruling of 2026-07-27 — "jeeb is only
/// cash on delivery", no unified_payment_gateway.
///
/// <para>This file REPLACES <c>Bff/UpgHealthProbePathTests</c>, which asserted the
/// opposite: that a unified-payment-gateway readiness probe MUST be registered.
/// That guard had inverted from a safety net into pressure to re-add UPG, so it
/// is deleted and its inverse pinned here.</para>
///
/// <para>WHY A BYTE SCAN. The claim "UPG is removed" was made and was wrong four
/// times in this batch. The most recent false absence came from grepping the
/// DEPLOYED assembly for <c>Services:UnifiedPayment</c> with an ASCII-only
/// search, which returned 0 — while the string was present as UTF-16, because
/// that is how the CLR stores string literals (ECMA-335 #US heap). A source grep
/// also cannot see it: source is not what ships. So this test scans the compiled
/// gateway assembly the way the runtime stores it.</para>
///
/// <para>POSITIVE CONTROL. A scanner that finds nothing is indistinguishable from
/// a scanner that is broken, so <see cref="Scanner_Finds_A_Config_Key_That_Is_Still_Present"/>
/// runs the exact same scan for a key that IS still wired up. If that control
/// ever goes green-by-absence, the absence assertions below are meaningless and
/// the control fails first and loudly.</para>
/// </summary>
public class NoPaymentGatewayDialGuardTests
{
    /// <summary>The configuration prefix every UPG destination was bound through.</summary>
    private const string UpgConfigPrefix = "Services:UnifiedPayment";

    /// <summary>A key that is still registered — proves the scanner can find things.</summary>
    private const string StillPresentControlKey = "Services:Geolocation:BaseUrl";

    [Fact]
    public void Scanner_Finds_A_Config_Key_That_Is_Still_Present()
    {
        // POSITIVE CONTROL — must find a live key, in UTF-16, in the shipped DLL.
        // If this fails, every absence assertion in this file is worthless.
        AssemblyContains(StillPresentControlKey, Encoding.Unicode).Should().BeTrue(
            "the UTF-16 scan must be able to find a config key that IS still bound "
            + $"({StillPresentControlKey}); if it cannot, the absence assertions below prove nothing");
    }

    [Fact]
    public void Compiled_Gateway_Assembly_Contains_No_UnifiedPayment_Config_Key()
    {
        // The real check: no UPG destination survives into the shipped assembly,
        // in either encoding. UTF-16 is the one that matters (string literals);
        // UTF-8 is checked too so an embedded resource or attribute cannot hide.
        AssemblyContains(UpgConfigPrefix, Encoding.Unicode).Should().BeFalse(
            $"'{UpgConfigPrefix}' must not be a live configuration string in the built gateway "
            + "(UTF-16 is how the CLR stores literals — an ASCII-only grep previously returned a false absence)");

        AssemblyContains(UpgConfigPrefix, Encoding.UTF8).Should().BeFalse(
            $"'{UpgConfigPrefix}' must not appear as UTF-8 bytes in the built gateway either");
    }

    [Fact]
    public void No_UnifiedPayment_Readiness_Probe_Is_Registered_Even_When_A_BaseUrl_Is_Configured()
    {
        // Adversarial: hand the gateway the very config key that used to light the
        // probe up. Nothing may register — the binding must be gone from the CODE,
        // not merely absent from committed config (config can be re-added by env).
        //
        // The control BaseUrl is set alongside it deliberately. Without it this
        // harness registers ZERO probes, and "no payment probe" would pass
        // vacuously — including if AddDownstreamHealthChecks stopped working
        // altogether. The control proves probes DO register here, so the absence
        // of the payment one is a real result rather than an empty list.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{UpgConfigPrefix}:BaseUrl"] = "http://127.0.0.1:10066",
                [StillPresentControlKey] = "http://127.0.0.1:10052",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        // Production-like: Development/Testing short-circuit and register no
        // downstream probes at all, which would be a false pass.
        services.AddDownstreamHealthChecks(config, new ProbeEnvironment("Production"));

        var registrations = services.BuildServiceProvider()
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
            .Value.Registrations;

        registrations.Should().Contain(r => r.Name == "geolocation-service",
            "POSITIVE CONTROL — a configured downstream must produce a probe in this harness; "
            + "if it does not, the payment-probe assertion below is vacuous");

        registrations.Should().NotContain(
            r => r.Name.Contains("payment", StringComparison.OrdinalIgnoreCase),
            "no readiness probe may dial a payment gateway under a cash-only policy");
    }

    private static bool AssemblyContains(string needle, Encoding encoding)
    {
        var path = typeof(JeebGateway.Financials.Cod.ICodSettlementLedger).Assembly.Location;
        File.Exists(path).Should().BeTrue(
            "the built gateway assembly must be resolvable for this scan to mean anything");

        var haystack = File.ReadAllBytes(path);
        return IndexOf(haystack, encoding.GetBytes(needle)) >= 0;
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return i;
        }
        return -1;
    }

    private sealed class ProbeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "JeebGateway.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
