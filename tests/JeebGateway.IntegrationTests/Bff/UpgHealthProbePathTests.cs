using System.Reflection;
using FluentAssertions;
using JeebGateway.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// Post-deploy regression guard for the unified-payment-gateway (UPG) readiness
/// probe repoint.
///
/// Context: the UPG decouple (sha-553711610c2a / main 9f05a991) REMOVED the
/// Elixir UPG's old GET /health route (now 404) and exposes the new decoupled
/// readiness surface at GET /api/v1/gateways (200). The gateway still probed
/// /health, so /health/aggregate and /health/ready 503'd with
/// unified-payment-gateway as the only failing downstream. The fix repoints ONLY
/// the UPG probe's healthPath from "health" to "api/v1/gateways" in
/// <see cref="HealthCheckExtensions"/>.
///
/// These tests pin the URL-join contract so a future edit cannot silently revert
/// the probe back to the removed /health route. They exercise the real
/// <see cref="HealthCheckExtensions.AddDownstreamHealthChecks"/> registration
/// path under a Production-like environment (Dev/Testing intentionally skip the
/// hard-fail downstream probes), with ONLY the UPG BaseUrl set so no live network
/// call is made and no other downstream is touched — keeping the existing
/// simulated-check tests (and S01-S08) untouched.
/// </summary>
public class UpgHealthProbePathTests
{
    private const string UpgName = "unified-payment-gateway";
    private const string UpgBaseUrl = "http://192.168.2.50:10066";

    [Fact]
    public void Upg_Probe_Resolves_To_Decoupled_Readiness_Path_Not_Removed_Health()
    {
        var uri = ResolveUpgProbeUri();

        // The decoupled UPG readiness surface (200). MUST NOT be the removed /health.
        uri.AbsoluteUri.Should().Be("http://192.168.2.50:10066/api/v1/gateways");
        uri.AbsolutePath.Should().Be("/api/v1/gateways");
        uri.AbsolutePath.Should().NotBe("/health",
            "the UPG decouple removed GET /health (404) — probing it falsely 503s the gateway aggregate");
    }

    [Fact]
    public void Upg_Probe_Is_Registered_As_A_Critical_Downstream_Readiness_Check()
    {
        var registration = GetUpgRegistration();

        // Critical-path: a genuine UPG readiness failure must remain fatal.
        registration.FailureStatus.Should().Be(HealthStatus.Unhealthy);
        registration.Tags.Should().Contain("ready");
        registration.Tags.Should().Contain("downstream");
    }

    private static HealthCheckRegistration GetUpgRegistration()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => Configuration());

        // Production-like environment so the hard-fail downstream probes are
        // registered (Development/Testing short-circuit and register none).
        var env = new ProbeEnvironment("Production");
        services.AddDownstreamHealthChecks(Configuration(), env);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<
            Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        return options.Registrations.Single(r => r.Name == UpgName);
    }

    private static Uri ResolveUpgProbeUri()
    {
        var registration = GetUpgRegistration();

        // The check is a UriHealthCheck (AspNetCore.HealthChecks.Uris). The
        // configured URI is not part of the public surface, so reach it via the
        // options/configured-uris held on the instance. The traversal is guarded
        // so any package-internal rename surfaces as a clear test failure rather
        // than a false green.
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(_ => Configuration());
        // AspNetCore.HealthChecks.Uris resolves IHttpClientFactory inside the
        // check factory; register the standard client infra so the factory can
        // construct the UriHealthCheck without a live network call.
        services.AddHttpClient();
        var provider = services.BuildServiceProvider();
        var instance = registration.Factory(provider);

        var uri = FindFirstUri(instance);
        uri.Should().NotBeNull(
            "the UPG readiness check must carry a configured probe URI; if this is null "
            + "the AspNetCore.HealthChecks.Uris internal shape changed and this guard needs updating");
        return uri!;
    }

    private static Uri? FindFirstUri(object instance)
    {
        // Breadth-first walk over fields/auto-properties of the check instance and
        // its options object, returning the first Uri (or first Uri in an
        // enumerable of Uri). Resilient to the exact internal field naming.
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(instance);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is null || !seen.Add(current)) continue;

            if (current is Uri directUri) return directUri;

            if (current is System.Collections.IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (item is Uri u) return u;
                    if (item is not null && IsInspectable(item)) queue.Enqueue(item);
                }
            }

            foreach (var field in current.GetType().GetFields(
                         BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                var value = field.GetValue(current);
                if (value is Uri fu) return fu;
                if (value is not null && (value is System.Collections.IEnumerable || IsInspectable(value)))
                    queue.Enqueue(value);
            }
        }

        return null;
    }

    private static bool IsInspectable(object value)
    {
        var type = value.GetType();
        // Only walk into our own / package types, not framework primitives.
        return type is { IsPrimitive: false, IsEnum: false }
               && type != typeof(string)
               && type.Namespace?.StartsWith("System.Reflection") != true;
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // ONLY the UPG BaseUrl — every other downstream BaseUrl is unset,
                // so AddDownstreamProbe skips them (no live calls, nothing else
                // registered). This isolates the assertion to the UPG probe.
                ["Services:UnifiedPayment:BaseUrl"] = UpgBaseUrl,
            })
            .Build();

    private sealed class ProbeEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "JeebGateway.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
