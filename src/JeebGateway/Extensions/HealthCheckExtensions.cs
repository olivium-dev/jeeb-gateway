using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Extensions;

/// <summary>
/// Registers downstream-service liveness probes used by the gateway's
/// <c>/health</c>, <c>/health/ready</c>, and <c>/health/live</c> endpoints.
/// Every check tags itself <c>"ready"</c> AND <c>"downstream"</c> so it
/// shows up under the readiness predicate and can be filtered to "just
/// upstream services" for dashboards.
///
/// JEB-67 / T-BE-031 AC2 — downstream failures are reported as
/// <see cref="HealthStatus.Unhealthy"/> so the aggregated <c>/health</c>
/// surface returns HTTP 503 (the default mapping in <c>MapHealthChecks</c>)
/// with the failing service named. Kubernetes liveness uses the separate
/// <c>/health/live</c> endpoint which only checks the <c>"live"</c> tag,
/// so a flaky upstream cannot cause a pod restart loop.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a URL-group health check per upstream service. Each check uses the
    /// same BaseUrl key as <see cref="ServiceClientExtensions.AddDownstreamClients"/>
    /// and probes <c>{BaseUrl}/health/ready</c> (the org-standard readyz path
    /// — see dotnet-healthchecks-readiness-liveness skill). Falls back to
    /// <c>{BaseUrl}/health</c> if the configured BaseUrl already ends in a
    /// health path segment.
    /// </summary>
    public static IServiceCollection AddDownstreamHealthChecks(
        this IServiceCollection services,
        IConfiguration config)
    {
        var checks = services.AddHealthChecks();

        AddDownstreamProbe(checks, config, "auth-service",         "Services:Auth:BaseUrl");
        AddDownstreamProbe(checks, config, "chat-service",         "Services:Chat:BaseUrl");
        AddDownstreamProbe(checks, config, "user-management",      "Services:UserManagement:BaseUrl");
        AddDownstreamProbe(checks, config, "wallet-service",       "Services:Wallet:BaseUrl");
        AddDownstreamProbe(checks, config, "matching",             "Services:Matching:BaseUrl");
        AddDownstreamProbe(checks, config, "notification-service", "Services:Notification:BaseUrl");
        AddDownstreamProbe(checks, config, "geolocation-service",  "Services:Geolocation:BaseUrl");
        AddDownstreamProbe(checks, config, "push-notification",    "Services:PushNotification:BaseUrl");
        AddDownstreamProbe(checks, config, "delivery-service",     "Services:Delivery:BaseUrl");

        return services;
    }

    private static void AddDownstreamProbe(
        IHealthChecksBuilder checks,
        IConfiguration config,
        string name,
        string baseUrlConfigKey)
    {
        var baseUrl = config[baseUrlConfigKey];

        // Skip probe registration entirely when the BaseUrl is unset (typical
        // for dev environments that don't spin up every upstream). An unset
        // URL means "we are not aggregating this service in this environment"
        // — registering a check pointing at an empty Uri would always fail and
        // wrongly mark the gateway Unhealthy.
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var readyEndpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health/ready");

        checks.AddUrlGroup(
            uri: readyEndpoint,
            name: name,
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready", "downstream" },
            timeout: TimeSpan.FromSeconds(3));
    }
}
