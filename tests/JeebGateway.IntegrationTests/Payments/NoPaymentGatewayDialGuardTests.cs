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
/// Regression gate for the COD ownership boundary. Unified-payment-gateway owns
/// the generic durable COD records; the stateless gateway must retain both the
/// owner transport and its database-backed readiness dependency.
///
/// <para>The byte scan verifies the owner configuration key in the compiled
/// artifact rather than inferring production behavior from source text. CLR
/// string literals are stored as UTF-16 in the #US heap.</para>
///
/// <para>POSITIVE CONTROL. A scanner that finds nothing is indistinguishable from
/// a scanner that is broken, so <see cref="Scanner_Finds_A_Config_Key_That_Is_Still_Present"/>
/// runs the exact same scan for a key that IS still wired up. If that control
/// ever goes green-by-absence, the absence assertions below are meaningless and
/// the control fails first and loudly.</para>
/// </summary>
public class CodOwnerBoundaryGuardTests
{
    /// <summary>The canonical durable COD-owner configuration key.</summary>
    private const string CodOwnerConfigKey = "UnifiedPaymentGateway:BaseUrl";

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
    public void Compiled_Gateway_Assembly_Contains_The_PrivateCodOwner_Config_Key()
    {
        AssemblyContains(CodOwnerConfigKey, Encoding.Unicode).Should().BeTrue(
            $"'{CodOwnerConfigKey}' is the durable COD owner configured by production code");
    }

    [Fact]
    public void CodOwnerReadinessProbeIsRegisteredWhenBaseUrlIsConfigured()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UnifiedPaymentGateway:BaseUrl"] = "http://127.0.0.1:10066",
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

        registrations.Should().ContainSingle(r => r.Name == "unified-payment-gateway",
            "the essential COD owner must gate production readiness");
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
