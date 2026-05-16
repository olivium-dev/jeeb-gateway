using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Extensions;

/// <summary>
/// Registers downstream-service liveness probes on the gateway's
/// <c>/health/ready</c> endpoint. Every check tags itself <c>"ready"</c> AND
/// <c>"downstream"</c> so it shows up under the readiness predicate and can be
/// filtered to "just upstream services" for dashboards.
///
/// Failures are reported as <see cref="HealthStatus.Degraded"/>, never
/// <see cref="HealthStatus.Unhealthy"/>. Rationale: the gateway can still serve
/// the endpoints that don't depend on the flaky upstream — Kubernetes should
/// not pull the pod out of the Service load balancer on a single backend hiccup.
/// Pages and alerts should fire on <c>Degraded</c>, not on liveness failure.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a URL-group health check per upstream service. Each check uses the
    /// same BaseUrl key as <see cref="ServiceClientExtensions.AddDownstreamClients"/>
    /// and probes <c>{BaseUrl}/health</c>.
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
        // wrongly mark the gateway Degraded.
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var healthEndpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "health");

        checks.AddUrlGroup(
            uri: healthEndpoint,
            name: name,
            failureStatus: HealthStatus.Degraded,
            tags: new[] { "ready", "downstream" },
            timeout: TimeSpan.FromSeconds(3));
    }
}
