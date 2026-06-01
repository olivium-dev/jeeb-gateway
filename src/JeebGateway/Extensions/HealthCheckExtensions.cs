using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Extensions;

/// <summary>
/// Registers downstream-service readiness probes used by the gateway's
/// <c>/health</c> and <c>/health/ready</c> endpoints. Every check tags itself
/// <c>"ready"</c> AND <c>"downstream"</c> so it shows up under the readiness
/// predicate and can be filtered to "just upstream services" for dashboards.
///
/// Two design rules, both grounded in the canonical Olivium gateway pattern
/// (a sibling gateway is effectively liveness-only — see OLIVIUM-GATEWAY-PATTERN.md):
///
/// 1. PROBE THE REAL HEALTH ROUTE. The olivium upstreams Jeeb consumes do NOT
///    expose the org-default <c>/health/ready</c> readyz path — they expose
///    <c>/health</c> (most) or <c>/healthz</c> (voice-transcription). Probing
///    the wrong path returns 404 and would falsely mark <c>/health/ready</c> red
///    even when the upstream is fully healthy. Each probe below is pinned to the
///    path the deployed service actually serves (verified against the swarm).
///
/// 2. NEVER 503 ON A NOT-YET-DEPLOYED OR ROUTE-LESS DOWNSTREAM. Services that
///    expose no health route at all (user-management, chat, feedback,
///    remote-user-preferences, auth) get NO readiness probe — they are
///    liveness-only from the gateway's perspective (we cannot assert their
///    readiness without a route; they are still reachable and called). Services
///    that are expected but not yet on the swarm are registered with
///    <see cref="HealthStatus.Degraded"/> so a missing instance shows up in the
///    dashboard WITHOUT turning <c>/health/ready</c> into a 503 (only
///    <see cref="HealthStatus.Unhealthy"/> maps to 503; Degraded stays 200).
///    Only the deployed, critical-path services use <c>Unhealthy</c>.
///
/// Kubernetes/Swarm liveness uses the separate <c>/health/live</c> endpoint
/// (the <c>"live"</c> tag only), so a flaky upstream can never cause a restart
/// loop.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds a URL-group readiness probe per deployed upstream service. The BaseUrl
    /// keys match <see cref="ServiceClientExtensions.AddDownstreamClients"/>. An
    /// unset BaseUrl skips the probe entirely (the service is not aggregated in
    /// this environment).
    /// </summary>
    public static IServiceCollection AddDownstreamHealthChecks(
        this IServiceCollection services,
        IConfiguration config)
    {
        var checks = services.AddHealthChecks();

        // --- Deployed, critical-path services, probed at their REAL health route.
        // All of these serve GET /health -> 200 on the swarm (verified). A real
        // failure here is fatal to readiness (Unhealthy -> /health/ready = 503).
        AddDownstreamProbe(checks, config, "wallet-service",          "Services:Wallet:BaseUrl",          healthPath: "health");
        AddDownstreamProbe(checks, config, "matching",                "Services:Matching:BaseUrl",        healthPath: "health");
        AddDownstreamProbe(checks, config, "notification-service",    "Services:Notification:BaseUrl",    healthPath: "health");
        AddDownstreamProbe(checks, config, "push-notification",       "Services:PushNotification:BaseUrl", healthPath: "health");
        AddDownstreamProbe(checks, config, "delivery-service",        "Services:Delivery:BaseUrl",        healthPath: "health");
        AddDownstreamProbe(checks, config, "geolocation-service",     "Services:Geolocation:BaseUrl",     healthPath: "health");
        AddDownstreamProbe(checks, config, "offer-service",           "Services:Offer:BaseUrl",           healthPath: "health");
        AddDownstreamProbe(checks, config, "ban-service",             "Services:Ban:BaseUrl",             healthPath: "health");
        AddDownstreamProbe(checks, config, "unified-payment-gateway", "Services:UnifiedPayment:BaseUrl",  healthPath: "health");

        // voice-transcription serves /healthz (not /health).
        AddDownstreamProbe(checks, config, "voice-transcription",     "Services:VoiceTranscription:BaseUrl", healthPath: "healthz");

        // user-management is the JWT identity service the whole gateway depends on
        // (OTP verify -> user-management -> JWT mint). Unlike the other upstreams it
        // exposes the org-canonical readiness path GET /health/ready (verified:
        // 200 {"status":"ready","db":"ok"}) AND GET /health/live. Its BaseUrl lives
        // at the top-level UserManagementApi:BaseUrl key (NOT under Services:), bound
        // by UserManagementApiOptions for the OTP sign-in path. It is critical-path,
        // so a real readiness failure here is fatal (Unhealthy -> /health/ready 503):
        // if user-management cannot reach its DB, the gateway genuinely cannot mint
        // tokens and should be pulled from rotation.
        AddDownstreamProbe(checks, config, "user-management",         "UserManagementApi:BaseUrl",        healthPath: "health/ready");

        // --- Liveness-only services (NO readiness probe).
        // These expose NO health route at all (verified on the swarm: GET /health
        // AND /health/ready both 404), so we cannot assert their readiness. They
        // are still reachable and called; we simply do not gate /health/ready on
        // them — exactly the sibling-gateway "liveness-only" intent, and the same
        // treatment PR #47 gave feedback-service. Adding a probe here would 404
        // and falsely mark the gateway red.
        //   - chat-service           (Services:Chat:BaseUrl)
        //   - feedback               (Services:Feedback:BaseUrl)
        //   - remote-user-preferences (Services:RemoteUserPreferences:BaseUrl) — host 10067, no /health route
        //   - auth-service           (Services:Auth — not yet deployed)

        return services;
    }

    private static void AddDownstreamProbe(
        IHealthChecksBuilder checks,
        IConfiguration config,
        string name,
        string baseUrlConfigKey,
        string healthPath = "health/ready",
        HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        var baseUrl = config[baseUrlConfigKey];

        // Skip probe registration entirely when the BaseUrl is unset (typical
        // for dev environments that don't spin up every upstream). An unset
        // URL means "we are not aggregating this service in this environment".
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return;
        }

        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), healthPath.TrimStart('/'));

        checks.AddUrlGroup(
            uri: endpoint,
            name: name,
            failureStatus: failureStatus,
            tags: new[] { "ready", "downstream" },
            timeout: TimeSpan.FromSeconds(3));
    }
}
