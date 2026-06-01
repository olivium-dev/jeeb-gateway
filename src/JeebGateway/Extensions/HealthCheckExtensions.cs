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
        AddDownstreamProbe(checks, config, "push-notification",    "Services:PushNotification:BaseUrl");
        AddDownstreamProbe(checks, config, "delivery-service",     "Services:Delivery:BaseUrl");

        // The six olivium upstreams Jeeb consumes are probed at their ACTUAL
        // health-route shapes (verified against the deployed services) rather
        // than the org-default {BaseUrl}/health/ready, which these services do
        // not expose. Probing the wrong path returns 404 and would falsely mark
        // /health/ready red even when the upstream is fully healthy.
        //   geolocation / offer / ban / unified_payment_gateway -> /health
        //   voice-transcription                                  -> /healthz
        //   feedback                                             -> NO health route
        // feedback therefore gets NO readiness probe — it is liveness-only from
        // the gateway's perspective (we cannot assert its readiness without a
        // route). It is still reachable; we simply do not gate /health/ready on
        // it. See dotnet-healthchecks-readiness-liveness skill.
        AddDownstreamProbe(checks, config, "geolocation-service",       "Services:Geolocation:BaseUrl",       healthPath: "health");
        AddDownstreamProbe(checks, config, "voice-transcription",       "Services:VoiceTranscription:BaseUrl", healthPath: "healthz");
        AddDownstreamProbe(checks, config, "offer-service",             "Services:Offer:BaseUrl",             healthPath: "health");
        AddDownstreamProbe(checks, config, "ban-service",               "Services:Ban:BaseUrl",               healthPath: "health");
        AddDownstreamProbe(checks, config, "unified-payment-gateway",   "Services:UnifiedPayment:BaseUrl",    healthPath: "health");

        return services;
    }

    private static void AddDownstreamProbe(
        IHealthChecksBuilder checks,
        IConfiguration config,
        string name,
        string baseUrlConfigKey,
        string healthPath = "health/ready")
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

        var readyEndpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), healthPath.TrimStart('/'));

        checks.AddUrlGroup(
            uri: readyEndpoint,
            name: name,
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready", "downstream" },
            timeout: TimeSpan.FromSeconds(3));
    }
}
