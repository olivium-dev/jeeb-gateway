using System;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace JeebGateway.Infrastructure;

/// <summary>
/// A3 / gwdbx W3-01 — fail-closed boot guard on the Redis IDistributedCache that holds the door-OTP
/// keys. StoreDurabilityGuard cannot see this fallback: the store TYPE is identical on both branches.
/// </summary>
internal static class RedisDurabilityGuard
{
    /// <summary>Config key (env form <c>Redis__ConnectionString</c>) selecting the shared Redis cache.</summary>
    internal const string ConnectionStringKey = "Redis:ConnectionString";

    /// <summary>Case-insensitive markers meaning the value is a placeholder, not a real endpoint.</summary>
    private static readonly string[] PlaceholderMarkers =
    {
        "REPLACE-WITH", "CHANGE-ME", "CHANGEME", "PLACEHOLDER", "DEV-ONLY", "EXAMPLE-HOST", "TODO",
    };

    /// <summary>
    /// Refuses to start (naming <see cref="ConnectionStringKey"/>) when Redis is unwired/misconfigured
    /// in a prod-like env. Armed for the WHOLE prod-like range; no-op in Development / Testing.
    /// </summary>
    /// <summary>Development/Testing are exempt; every other environment is prod-like.</summary>
    internal static bool IsExempt(IHostEnvironment environment)
        => environment is null
           || environment.IsDevelopment()
           || string.Equals(environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase);

    public static void EnsureWired(IConfiguration? configuration, IHostEnvironment environment)
    {
        // Development/Testing exempt, everything else armed. (W5-11 deleted StoreDurabilityGuard
        // with the gateway database; this guard keeps its own copy of that exemption.)
        if (IsExempt(environment))
        {
            return;
        }

        var connectionString = configuration?[ConnectionStringKey];

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw Fail(environment, "is not set");
        }

        foreach (var marker in PlaceholderMarkers)
        {
            if (connectionString.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                throw Fail(environment, $"is a placeholder value (contains '{marker}')");
            }
        }
    }

    private static InvalidOperationException Fail(IHostEnvironment environment, string problem)
        => new(
            $"FAIL-CLOSED (A3/W3-01): '{ConnectionStringKey}' (environment variable Redis__ConnectionString) " +
            $"{problem} in the prod-like environment '{environment.EnvironmentName}'. That key selects the " +
            "shared Redis IDistributedCache which is the ONLY home of the door-OTP state — the in-app " +
            "handover code (IHandoverCodeStore) and the at-door OTP attempt/lockout counters. Without it the " +
            "gateway boots on the in-process AddDistributedMemoryCache fallback, so a code minted on one " +
            "replica cannot be verified at the door on another and every code is lost on restart, behind a " +
            "green health check. Refusing to start. Set " + ConnectionStringKey + " to the cluster's Redis endpoint.");
}
