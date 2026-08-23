using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

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
    /// unset BaseUrl skips the generic probe (the service is not aggregated in
    /// that environment). Staging is the deliberate exception: its mandatory
    /// settlement-owner gate is always registered and fails closed on missing config.
    ///
    /// In the Development and Testing environments the hard-fail downstream probes
    /// are NOT registered: those environments carry localhost dev-default BaseUrls
    /// (so the typed clients can bind) but do not actually spin up every backend,
    /// so probing them would 503 <c>/health/ready</c> and <c>/health/aggregate</c>
    /// against unreachable ports. This restores the documented contract — "local dev
    /// does not have to spin up every backend" — that base-config dev defaults
    /// otherwise broke. Production (and any non-Dev/Testing env) registers the
    /// probes exactly as before.
    /// </summary>
    public static IServiceCollection AddDownstreamHealthChecks(
        this IServiceCollection services,
        IConfiguration config,
        IHostEnvironment environment)
    {
        // Dev/Testing: skip hard-fail downstream readiness probes. The dev-default
        // localhost BaseUrls in base appsettings.json are not reachable here, and
        // turning them into Unhealthy URL-group checks would falsely 503 the
        // readiness/aggregate surfaces. The "self" (live) and any in-process
        // checks remain registered, so the readiness predicate still has the
        // process-liveness signal and tests that inject their own failing check
        // (tagged "ready") continue to drive a deterministic 503.
        if (environment.IsDevelopment()
            || environment.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase))
        {
            return services;
        }

        var checks = services.AddHealthChecks();

        // --- Deployed, critical-path services, probed at their REAL health route.
        // All of these serve GET /health -> 200 on the swarm (verified). A real
        // failure here is fatal to readiness (Unhealthy -> /health/ready = 503).
        AddDownstreamProbe(checks, config, "wallet-service",          "WalletServiceApi:BaseUrl",         healthPath: "health");
        // matching-service readiness probe REMOVED (JEBV4-220 / E25) — the
        // standalone matching-service read path is retired; nothing dials it.
        AddDownstreamProbe(checks, config, "notification-service",    "ServiceNotificationClient:BaseUrl", healthPath: "health");
        // 608debf outage: the credential chain failed closed while every health surface stayed
        // green (the URL probe above hits the token-free public /health). In-process, no I/O.
        checks.AddCheck<JeebGateway.Notifications.NotificationCredentialHealthCheck>(
            JeebGateway.Notifications.NotificationCredentialHealthCheck.Name,
            failureStatus: HealthStatus.Unhealthy,
            tags: new[] { "ready" });
        AddDownstreamProbe(checks, config, "push-notification",       "PushNotificationServiceApi:BaseUrl", healthPath: "health");
        AddDownstreamProbe(checks, config, "delivery-service",        "Services:Delivery:BaseUrl",        healthPath: "health");
        AddDownstreamProbe(checks, config, "geolocation-service",     "Services:Geolocation:BaseUrl",     healthPath: "health");
        AddDownstreamProbe(checks, config, "offer-service",           "Services:Offer:BaseUrl",           healthPath: "health");
        AddDownstreamProbe(checks, config, "ban-service",             "Services:Ban:BaseUrl",             healthPath: "health");
        // gwdbx W2-R11: settlement-service owns the money rows the gateway used to hold. In
        // Staging this gate is registered unconditionally and validates BOTH the mounted SERVICE
        // credential and the exact upstream readiness route, so omitted config cannot disappear
        // from the roster and false-green the gateway. Other environments retain the URL probe.
        if (environment.IsStaging())
        {
            services.Configure<JeebGateway.Financials.SettlementServiceOptions>(
                config.GetSection(JeebGateway.Financials.SettlementServiceOptions.SectionName));
            services.AddHttpClient(
                JeebGateway.Financials.SettlementServiceReadinessHealthCheck.HttpClientName,
                http => http.Timeout = TimeSpan.FromSeconds(5));
            checks.AddCheck<JeebGateway.Financials.SettlementServiceReadinessHealthCheck>(
                "settlement-service",
                failureStatus: HealthStatus.Unhealthy,
                tags: new[] { "ready", "downstream" });
        }
        else
        {
            AddDownstreamProbe(checks, config, "settlement-service", "Services:Settlement:BaseUrl", healthPath: "health/ready");
        }

        // bundler-service is NOT a URL-group probe. A Host-matching reverse proxy answers 200 with an
        // empty body on every path when no site block matches, so a status code alone cannot fail.
        if (!string.IsNullOrWhiteSpace(config[JeebGateway.Cms.BundlerCmsSurfaceStore.BaseUrlConfigurationKey]))
        {
            checks.AddCheck<JeebGateway.Cms.BundlerServiceHealthCheck>(
                JeebGateway.Cms.BundlerServiceHealthCheck.Name,
                failureStatus: HealthStatus.Degraded,
                tags: new[] { "ready", "downstream" });
        }

        // role-service probe REMOVED 2026-08-16 (owner O8): the service is retired, so the
        // check it added at D1 has nothing to probe. It was the roster's only Degraded entry.

        // unified-payment-gateway (Elixir UPG) was decoupled (sha-553711610c2a /
        // main 9f05a991): the old GET /health route was REMOVED (now 404) and the
        // new decoupled surface is GET /api/v1/gateways (verified 200 on the swarm,
        // trailing-slash variant also 200). Probe the readiness path it actually
        // serves; bare /health here 404s and falsely 503s the gateway aggregate.
        // UPG probe REMOVED 2026-07-27 by owner ruling: "do not use UPG, jeeb is only cash on
        // delivery". Services:UnifiedPayment:BaseUrl is gone from committed config (it was the last
        // live 192.168.2.50 destination), so probing it would dial a banned host and fail a
        // CRITICAL readiness check for a service Jeeb deliberately no longer uses. Do not re-add.

        // voice-transcription owns durable retry state; /readyz verifies its
        // dependencies, whereas /healthz is deliberately liveness-only.
        AddDownstreamProbe(checks, config, "voice-transcription",     "Services:VoiceTranscription:BaseUrl", healthPath: "readyz");

        // user-management is the identity service the gateway proxies to via the
        // NSwag-generated ServiceUserManagementClient (UserController). Unlike the
        // other upstreams it exposes the org-canonical readiness path
        // GET /health/ready (verified: 200 {"status":"ready","db":"ok"}) AND
        // GET /health/live. Its BaseUrl lives at the top-level
        // UserManagementServiceApi:BaseUrl key (NOT under Services:), matching the
        // salehly-gateway sibling convention. It is critical-path, so a real
        // readiness failure here is fatal (Unhealthy -> /health/ready 503): if
        // user-management cannot reach its DB, the gateway genuinely cannot serve
        // identity traffic and should be pulled from rotation.
        AddDownstreamProbe(checks, config, "user-management",         "UserManagementServiceApi:BaseUrl", healthPath: "health/ready");

        // --- Liveness-only services (NO readiness probe).
        // These expose NO health route at all (verified on the swarm: GET /health
        // AND /health/ready both 404), so we cannot assert their readiness. They
        // are still reachable and called; we simply do not gate /health/ready on
        // them — exactly the sibling-gateway "liveness-only" intent, and the same
        // treatment PR #47 gave feedback-service. Adding a probe here would 404
        // and falsely mark the gateway red.
        //   - chat-service           (ChatServiceApi:BaseUrl) — salehly-mirrored top-level key
        //   - feedback               (FeedbackServiceApi:BaseUrl) — salehly-mirrored top-level key
        //   - remote-user-preferences (RemoteUserPreferencesServiceApi:BaseUrl) — host 10067, no /health route
        //   - auth-service           (Services:Auth — not yet deployed)
        //   - one-time-password      (Services:ServiceOTP:BaseUrl / ServiceOTPApi:BaseUrl — host 10037).
        //       OTPApi (olivium-dev/one-time-password) maps ONLY its controllers
        //       (api/OTP/send, api/OTP/validate, api/OTP/check) — it serves NO
        //       /health, /health/ready, or /healthz route (verified against
        //       OTPApi/Program.cs: UseRouting + MapControllers, no MapHealthChecks).
        //       A URL-group probe would 404 and falsely 503 /health/ready, so the
        //       OTP upstream is liveness-only from the gateway's perspective —
        //       same treatment as feedback-service (PR #47).

        // --- Degraded (non-fatal) downstream probe.
        // realtime-comunication-service (olivium-dev/realtime-comunication-service,
        // Elixir/Phoenix 'LiveComm') is now deployed on the Jeeb swarm at
        // Services:Realtime:BaseUrl (192.168.2.50:10069). It serves GET /health.
        // We probe it as Degraded (not Unhealthy): a missing realtime instance
        // surfaces in /health/aggregate WITHOUT 503-ing the gateway's /health/ready,
        // because the FeatureFlags:UseUpstream:Realtime kill switch independently
        // gates the publish path. Serves JEB-1453/1449/1432/626/444/50/51/52.
        AddDownstreamProbe(checks, config, "realtime-comunication-service", "Services:Realtime:BaseUrl", healthPath: "health", failureStatus: HealthStatus.Degraded);

        // --- Degraded (non-fatal) downstream probe.
        // contract-signing-service (FastAPI) is now deployed on the Jeeb swarm at
        // Services:ContractSigning:BaseUrl (192.168.2.50:10071). It serves
        // GET /health. We probe it as Degraded (not Unhealthy): a missing instance
        // surfaces in /health/aggregate WITHOUT 503-ing /health/ready, because the
        // FeatureFlags:UseUpstream:ContractSigning kill switch independently gates
        // the routing path.
        AddDownstreamProbe(checks, config, "contract-signing-service", "Services:ContractSigning:BaseUrl", healthPath: "health", failureStatus: HealthStatus.Degraded);

        // --- Degraded (non-fatal) downstream probe.
        // cdn-service is now deployed on the Jeeb swarm at Services:Cdn:BaseUrl
        // (192.168.2.50:10072). Unlike most upstreams it exposes the org-canonical
        // readiness path GET /health/ready (not bare /health). We probe it as
        // Degraded (not Unhealthy): a missing instance surfaces in /health/aggregate
        // WITHOUT 503-ing /health/ready, because the FeatureFlags:UseUpstream:Cdn
        // kill switch independently gates the routing path. Serves JEB-527/519/59.
        AddDownstreamProbe(checks, config, "cdn-service", "Services:Cdn:BaseUrl", healthPath: "health/ready", failureStatus: HealthStatus.Degraded);

        // --- Degraded (non-fatal) downstream probe.
        // form-builder-service is now deployed on the Jeeb swarm at
        // Services:FormBuilder:BaseUrl (192.168.2.50:10070). It is a FastAPI app
        // that exposes NO /health or /health/ready route — probing those would 404
        // and falsely degrade. Its real liveness signal is GET /openapi.json (200,
        // always served by FastAPI, no auth, no data dependency). We pin the probe
        // to /openapi.json and mark it Degraded (not Unhealthy): a missing instance
        // surfaces in /health/aggregate WITHOUT 503-ing /health/ready, because the
        // FeatureFlags:UseUpstream:FormBuilder kill switch independently gates the
        // routing path.
        AddDownstreamProbe(checks, config, "form-builder-service", "Services:FormBuilder:BaseUrl", healthPath: "openapi.json", failureStatus: HealthStatus.Degraded);

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
