using System.Text;
using JeebGateway.Admin;
using JeebGateway.Availability;
using JeebGateway.Disputes;
using JeebGateway.Disputes.V2;
using JeebGateway.Extensions;
using JeebGateway.Financials;
using JeebGateway.Kyc;
using JeebGateway.Middleware;
using JeebGateway.NotificationPreferences;
using JeebGateway.ProhibitedItems;
using JeebGateway.StateService;
using JeebGateway.Ratings;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Push;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Requests.OtpHandover;
using JeebGateway.Security;
using JeebGateway.Services;
using JeebGateway.Tokens;
using JeebGateway.Tracking;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using JeebGateway.Calls;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

// ---------------------------------------------------------------------------
// Forwarded headers (PR #32 review B2).
//
// jeeb-gateway sits behind a load balancer / reverse proxy that terminates
// TLS and forwards the original client address via X-Forwarded-For. Without
// UseForwardedHeaders, HttpContext.Connection.RemoteIpAddress is the LB's
// internal address, which collapses the per-IP rate limit
// (AC-GatewayRateLimit) to a single bucket shared across every client.
//
// Trusted-proxy allowlist comes from ForwardedHeaders:KnownProxies in
// configuration (env / sealed secret). Empty list intentionally leaves the
// default "loopback only" trust so misconfigured deploys do not silently
// trust attacker-supplied X-Forwarded-For.
// ---------------------------------------------------------------------------
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Drop the default "exactly one hop" restriction — production traffic
    // routes through cloudflared + Swarm ingress (≥ 2 hops). The KnownProxies
    // / KnownNetworks allowlist below is the actual trust boundary.
    options.ForwardLimit = null;

    var knownProxies = builder.Configuration
        .GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>();
    foreach (var proxy in knownProxies)
    {
        if (System.Net.IPAddress.TryParse(proxy, out var ip))
        {
            options.KnownProxies.Add(ip);
        }
    }

    var knownNetworks = builder.Configuration
        .GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>();
    foreach (var cidr in knownNetworks)
    {
        var parts = cidr.Split('/', 2);
        if (parts.Length == 2
            && System.Net.IPAddress.TryParse(parts[0], out var net)
            && int.TryParse(parts[1], out var prefix))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(net, prefix));
        }
    }
});

// ---------------------------------------------------------------------------
// Edge security (T-backend-032): CORS, rate limiting, JWT bearer, headers.
// ---------------------------------------------------------------------------
builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.SectionName));

// JWT bearer scheme — validates Authorization: Bearer <jwt> issued by TokenService.
// Endpoints retain the existing UserIdentity helper which also accepts the
// edge-injected X-User-Id header for MVP / tests, so registering a scheme
// here does NOT make the gateway reject untokened MVP traffic.
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
var signingBytes = Encoding.UTF8.GetBytes(jwt.SigningKey);
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwt.Issuer,
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(signingBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "roles"
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddCors(options =>
{
    var sec = builder.Configuration.GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();
    options.AddPolicy(sec.Cors.PolicyName, policy =>
    {
        policy.WithOrigins(sec.Cors.AllowedOrigins)
              .WithMethods(sec.Cors.AllowedMethods)
              .WithHeaders(sec.Cors.AllowedHeaders)
              .WithExposedHeaders(sec.Cors.ExposedHeaders)
              .SetPreflightMaxAge(TimeSpan.FromSeconds(sec.Cors.PreflightMaxAgeSeconds));
        if (sec.Cors.AllowCredentials)
        {
            policy.AllowCredentials();
        }
    });
});

builder.Services.AddJeebRateLimiting();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
    {
        Title = "Jeeb Gateway",
        Version = "v1",
        Description = "BFF gateway aggregating downstream Jeeb services."
    });
});

// Health checks — the live probe ("self") returns 200 if the process is up;
// downstream-service probes are wired below via AddDownstreamHealthChecks and
// only run under the readiness predicate.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("process alive"), tags: new[] { "live" });

// ---------------------------------------------------------------------------
// BFF aggregation (JEB-67 / T-BE-031) + skeleton (T-migrate-gateway-shell)
//
// AddBffAggregation wires the cross-cutting BFF concerns:
//   - ServiceAuthOptions    (X-Service-Auth HMAC, AC3)
//   - DownstreamServicesOptions + BffStartupValidator (AC1 — fail boot when
//     required downstream BaseUrls are missing in non-Dev/Testing envs)
//   - IHttpContextAccessor for BearerForwardingHandler (AC3 — JWT forward)
//
// AddDownstreamClients registers a named HttpClient + DelegatingHandlers
// (BearerForwardingHandler + ServiceAuthSigningHandler) + Polly resilience
// pipeline (retry + circuit breaker + per-attempt timeout) per upstream
// service. Generated NSwag typed clients (Services/Generated/*Client.cs)
// hang off these named registrations once each per-controller migration
// ticket lands. See Extensions/ServiceClientExtensions.cs and
// scripts/regenerate-clients.sh.
//
// AddDownstreamHealthChecks registers a /health/ready URL-group probe per
// upstream (tagged "ready" + "downstream", failureStatus: Unhealthy so the
// aggregated /health endpoint returns HTTP 503 per AC2). Unset BaseUrls
// silently skip — local dev does not have to spin up every backend.
// ---------------------------------------------------------------------------
builder.Services.AddBffAggregation(builder.Configuration);
// AddDownstreamClients also registers the typed IContractSigningServiceClient
// (contract-signing-service / immutable contract templates + per-party
// signatures; consumed by ContractSigningController, gated by
// FeatureFlags:UseUpstream:ContractSigning which defaults OFF — the service is
// not yet deployed, BaseUrl is a placeholder). It serves the versioned Jeeb ToS
// template jeeb_tos_v1 (JEB-40/JEB-41) via RegisterTemplateAsync/SignAsync. See
// the contract-signing block in Extensions/ServiceClientExtensions.cs.
// AddDownstreamClients also registers the typed IFormBuilderServiceClient
// (form-builder-service / dynamic forms; consumed by FormBuilderController,
// gated by FeatureFlags:UseUpstream:FormBuilder which defaults OFF — the
// service is not yet deployed, BaseUrl is a placeholder). See the form-builder
// block in Extensions/ServiceClientExtensions.cs.
builder.Services.AddDownstreamClients(builder.Configuration);
builder.Services.AddDownstreamHealthChecks(builder.Configuration, builder.Environment);

// EXACT-SALEHLY MIRROR (RemoteUserPreferences): UserPreferencesController consumes
// the NSwag-generated ServiceRemoteUserPreferencesClient directly, exactly as
// salehly-gateway does (Program.cs:207-213). The client is scoped and built from
// the "remote-user-preferences" named HttpClient (which carries the standard
// bearer/X-Service-Auth/resilience pipeline) with its baseUrl read from salehly's
// config key RemoteUserPreferencesServiceApi:BaseUrl (prod: http://192.168.2.50:10067/).
// There is NO UseUpstream flag gate on this controller — salehly's controller
// always forwards to the upstream (no 503-without-calling path).
builder.Services.AddScoped<JeebGateway.Services.Generated.ServiceRemoteUserPreferences.ServiceRemoteUserPreferencesClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("remote-user-preferences");
    var baseUrl = builder.Configuration["RemoteUserPreferencesServiceApi:BaseUrl"];
    return new JeebGateway.Services.Generated.ServiceRemoteUserPreferences.ServiceRemoteUserPreferencesClient(baseUrl, client);
});

// Chat (ChatServiceApi) — salehly sibling mirror. The NSwag-generated
// ServiceChatClient (Services/ServiceChatClient.cs, namespace
// JeebGateway.service.ServiceChat) is registered exactly as salehly-gateway does
// it: a named IHttpClientFactory client "ServiceChatClient" bound to
// ChatServiceApi:BaseUrl, plus a scoped typed-client instance that pulls the
// pooled HttpClient from the factory and constructs the client with the
// configured base URL. ChatController consumes the typed client directly as a
// passthrough REST shim over the generic chat-service (channels, messages,
// members, sessions). This replaces the former jeeb-specific 1:1 conversation
// BFF (ChatServiceClient + Redis topology map + SignalR ChatHub/ChatDispatcher),
// which has been removed.
builder.Services.AddHttpClient("ServiceChatClient", client =>
{
    var apiUrl = builder.Configuration["ChatServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServiceChat.ServiceChatClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceChatClient");
    var baseUrl = builder.Configuration["ChatServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceChat.ServiceChatClient(baseUrl, client);
});

// Notification (ServiceNotificationClient) — salehly sibling mirror. The
// NSwag-generated ServiceNotificationClient (Services/ServiceNotificationClient.cs,
// namespace JeebGateway.service.ServiceNotification) is registered exactly as
// salehly-gateway does it: a named IHttpClientFactory client
// "ServiceNotificationClient" bound to the ServiceNotificationClient:BaseUrl
// config key, plus a scoped typed-client instance that pulls the pooled
// HttpClient from the factory and constructs the client with the configured base
// URL. NotificationController consumes the typed client directly as a passthrough
// REST shim over the generic notification-service (list-by-receiver,
// mark-read/unread, bulk mark, health). This replaces the former jeeb-specific
// notification read BFF (NotificationServiceClient + INotificationServiceClient +
// NotificationsController under /users/me/notifications), which has been removed.
//
// NOTE on the config key: salehly registers the named client against config key
// "ServiceNotificationClient" (Program.cs:122) but its scoped registration reads
// "NotificationServiceApi:BaseUrl" (Program.cs:242) — a key that does not exist
// in salehly's appsettings, so salehly's client receives a null base URL. jeeb
// uses the CORRECT key "ServiceNotificationClient:BaseUrl" in BOTH places so the
// client actually resolves the upstream address.
builder.Services.AddHttpClient("ServiceNotificationClient", client =>
{
    var apiUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServiceNotification.ServiceNotificationClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceNotificationClient");
    var baseUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
    return new JeebGateway.service.ServiceNotification.ServiceNotificationClient(baseUrl, client);
});

// PushNotification (ServicePushNotificationClient) — salehly sibling mirror.
// The NSwag-generated ServicePushNotificationClient
// (Services/ServicePushNotificationClient.cs, namespace
// JeebGateway.service.ServicePushNotification) is registered exactly as
// salehly-gateway does it (Program.cs:119 + Program.cs:214): a named
// IHttpClientFactory client "ServicePushNotificationClient" bound to the
// PushNotificationServiceApi:BaseUrl config key, plus a scoped typed-client
// instance that pulls the pooled HttpClient from the factory and constructs the
// client with the configured base URL. PushNotificationController consumes the
// typed client directly as a passthrough REST shim over the generic
// push-notification service (register/delete device, send-to-device/user,
// broadcast, health). This replaces the former jeeb-specific device-register
// passthrough (PushController + IPushNotificationClient + PushNotificationClient),
// which has been removed.
builder.Services.AddHttpClient("ServicePushNotificationClient", client =>
{
    var apiUrl = builder.Configuration["PushNotificationServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServicePushNotification.ServicePushNotificationClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServicePushNotificationClient");
    var baseUrl = builder.Configuration["PushNotificationServiceApi:BaseUrl"];
    return new JeebGateway.service.ServicePushNotification.ServicePushNotificationClient(baseUrl, client);
});

// Feedback (ServiceFeedbackClient) — salehly sibling mirror.
// The NSwag-generated ServiceFeedbackClient
// (Services/Clients/ServiceFeedbackClient.cs, namespace
// JeebGateway.service.ServiceFeedback) is registered exactly as salehly-gateway
// does it (Program.cs:112 ConfigureNamedClient + Program.cs:159 scoped factory):
// a named IHttpClientFactory client "ServiceFeedbackClient" bound to the
// FeedbackServiceApi:BaseUrl config key, plus a scoped typed-client instance
// that pulls the pooled HttpClient from the factory and constructs the client
// with the configured base URL. FeedbackController consumes the typed client
// directly (comment CRUD, grouped, rating) as a passthrough REST shim over the
// feedback-service. This replaces the former jeeb-specific hand-coded
// IFeedbackServiceClient / FeedbackServiceClient (3-method submit+read seam),
// which has been removed.
//
// The technician-review endpoint additionally orchestrates catalog-service and
// user-management-service, so their NSwag clients are registered the same way
// (named + scoped, bound to CatalogServiceApi / UserManagementServiceApi),
// matching salehly Program.cs:115/113 + 183/167. These two are byte-faithful
// salehly NSwag artifacts consumed ONLY by TechnicianReviewService — no other
// jeeb code depends on them; the jeeb auth/role-switch surfaces keep their own
// hand-coded user-management clients.
builder.Services.AddHttpClient("ServiceFeedbackClient", client =>
{
    var apiUrl = builder.Configuration["FeedbackServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServiceFeedback.ServiceFeedbackClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceFeedbackClient");
    var baseUrl = builder.Configuration["FeedbackServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceFeedback.ServiceFeedbackClient(baseUrl, client);
});

builder.Services.AddHttpClient("ServiceCatalogClient", client =>
{
    var apiUrl = builder.Configuration["CatalogServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServiceCatalog.ServiceCatalogClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceCatalogClient");
    var baseUrl = builder.Configuration["CatalogServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceCatalog.ServiceCatalogClient(baseUrl, client);
});

builder.Services.AddHttpClient("ServiceUserManagementClient", client =>
{
    var apiUrl = builder.Configuration["UserManagementServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});
builder.Services.AddScoped<JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceUserManagementClient");
    var baseUrl = builder.Configuration["UserManagementServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient(baseUrl, client);
});

// Technician-review orchestrator (feedback + catalog + user-management),
// matching salehly Program.cs:254. Scoped because it depends on the scoped
// NSwag clients above.
builder.Services.AddScoped<JeebGateway.Services.ITechnicianReviewService, JeebGateway.Services.TechnicianReviewService>();

// T-migrate-gateway-proxies (PR-A): per-service kill switches. Each
// controller migrated in this PR checks the matching flag and falls
// back to the in-memory store when false. PR-B flips defaults to true
// and removes the in-memory stores.
builder.Services.Configure<UpstreamFeatureFlags>(
    builder.Configuration.GetSection(UpstreamFeatureFlags.SectionName));

// OpenTelemetry
var serviceName = "jeeb-gateway";
var otlpEndpoint = builder.Configuration["Otel:Endpoint"] ?? "http://localhost:4317";

builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(serviceName))
    .WithTracing(tracing =>
    {
        tracing
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // T-BE-028 / JEB-64 — dispute case open / resolve spans.
            .AddSource(DisputeCaseTelemetry.ActivitySourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // T-backend-050 — Jeeb-owned per-endpoint latency meter.
            .AddMeter(RequestLatencyMetrics.MeterName)
            // T-BE-028 / JEB-64 — dispute case counters & histograms.
            .AddMeter(DisputeCaseTelemetry.MeterName)
            // Explicit buckets keep the 400ms p95 SLO on a bucket boundary so
            // histogram_quantile() does not round across a wide bucket (T-backend-050).
            .AddView(
                instrumentName: "http.server.request.duration",
                metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new[]
                    {
                        0.005, 0.01, 0.025, 0.05, 0.1, 0.2,
                        0.3, 0.4, 0.5, 0.75, 1.0, 2.5, 5.0, 10.0
                    }
                })
            // Same boundaries on the Jeeb-owned histogram so dashboards and
            // alerts can pivot to it without re-bucketing.
            .AddView(
                instrumentName: RequestLatencyMetrics.HistogramName,
                metricStreamConfiguration: new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = new[]
                    {
                        0.005, 0.01, 0.025, 0.05, 0.1, 0.2,
                        0.3, 0.4, 0.5, 0.75, 1.0, 2.5, 5.0, 10.0
                    }
                })
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint))
            // T-backend-050 — Prometheus scrape endpoint mounted on /metrics
            // (see MapPrometheusScrapingEndpoint below). 1-minute scrape
            // granularity is enforced from the Prometheus side via the
            // scrape_interval on the jeeb-gateway job (observability/alerts).
            .AddPrometheusExporter();
    });

// T-backend-050 — singleton wrapper around the latency Meter. The Meter is
// owned by DI so its lifetime matches the host's, which keeps the OTel
// MeterProvider's subscription alive for the life of the process.
builder.Services.AddSingleton<RequestLatencyMetrics>();

// ===========================================================================
// LEGACY IN-MEMORY SERVICE REGISTRATIONS — DO NOT EXTEND
//
// Everything below this banner is the MVP/in-memory implementation backing
// the controllers under Controllers/, every one of which is now marked
// [Obsolete]. These registrations stay intact because per-controller
// migration tickets will replace each store with a call into the
// NSwag-generated client registered above via AddDownstreamClients.
//
// When you migrate a controller:
//   1. Run scripts/regenerate-clients.sh to refresh the typed client for the
//      relevant upstream service.
//   2. Register the typed client on top of the named HttpClient registered
//      in Extensions/ServiceClientExtensions.cs (the named registration
//      already carries the resilience pipeline).
//   3. Replace the controller's dependency on the in-memory store with the
//      generated client (wrapped if you need an adapter contract).
//   4. Remove the matching AddSingleton<I*Store, InMemory*Store>() line below.
//   5. Remove the [Obsolete] annotation from the controller in the same PR.
//
// Track per-controller migrations against GATEWAY-REMEDIATION-PLAN.md.
// ===========================================================================

// Cash settlement + receipt API (T-backend-016 / JEEB-34).
//
// SettlementService re-computes the Jeeb fee (commission % per tier +
// 2% insurance, min 1000 LBP) from the row's tier and posts a single
// best-effort ledger entry via ISettlementLedgerClient. The settlement row
// is the gateway-side system of record; the ledger post is idempotent on the
// settlement id. Cash settlement is a Jeeb product concern and keeps its own
// slim ledger contract in the Financials module — it does NOT ride on the
// wallet integration, which now mirrors the salehly-gateway sibling's
// upstream wallet API byte-for-byte (WalletController + ServiceWalletClient).
builder.Services.AddSingleton<ISettlementStore, InMemorySettlementStore>();
builder.Services.AddSingleton<ISettlementLedgerClient, InMemorySettlementLedgerClient>();
builder.Services.AddSingleton<ISettlementService, SettlementService>();

// ===========================================================================
// Wallet integration — EXACT mirror of the salehly-gateway sibling.
//
// jeeb-gateway proxies all wallet traffic through the NSwag-generated
// ServiceWalletClient (Services/ServiceWalletClient.cs, namespace
// JeebGateway.service.ServiceWallet) exactly as salehly-gateway does. The
// client is a named IHttpClientFactory client bound to WalletServiceApi:BaseUrl
// via ConfigureNamedClient, with a scoped typed-client instance that hands the
// named HttpClient to the generated constructor.
//
// Controllers/WalletController.cs is the byte-faithful salehly WalletController
// (routes under /api/Wallet: system-wallet, holder/add, holder/{holderId}/Add,
// {holderId}/{walletId}/deactivate{,/force-deactivate},
// holder/{holderId}/deactivate{,/force-deactivate}, holder/wallets[authorized]).
// ===========================================================================
void ConfigureNamedClient(string name, string configKey)
{
    builder.Services.AddHttpClient(name, client =>
    {
        var apiUrl = builder.Configuration[$"{configKey}:BaseUrl"];
        if (!string.IsNullOrEmpty(apiUrl))
        {
            client.BaseAddress = new Uri(apiUrl);
        }
    });
}

ConfigureNamedClient("ServiceWalletClient", "WalletServiceApi");

builder.Services.AddScoped<JeebGateway.service.ServiceWallet.ServiceWalletClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceWalletClient");
    var baseUrl = builder.Configuration["WalletServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceWallet.ServiceWalletClient(baseUrl, client);
});

// Notification preferences (T-backend-031).
// In-memory implementation for MVP; swap for an NSwag-generated notification-service
// client once the downstream preferences endpoints land.
builder.Services.AddSingleton<INotificationPreferencesStore, InMemoryNotificationPreferencesStore>();

// Push notification pipeline (T-backend-022).
//
// One unified outbound surface for every push-eligible trigger: new offers,
// offer acceptance, status changes, chat, KYC, rating reminders. The service
// applies the user's NotificationPreferences (always-on triggers bypass),
// resolves registered device tokens, fans out through the platform-matched
// IPushTransport, and queues a single 30-second retry on first-attempt
// failure.
//
// Production swap: the in-memory FCM/APNs transports become real Google FCM
// HTTP v1 and Apple APNs HTTP/2 clients (NSwag-generated against the
// notification-service surface, per the BFF aggregation pattern); the
// in-memory device-token store becomes a Postgres-backed implementation
// alongside the per-user row in 0006.
builder.Services.Configure<PushOptions>(builder.Configuration.GetSection(PushOptions.SectionName));
// The device-register HTTP surface is now the salehly-mirrored
// PushNotificationController, backed by the NSwag ServicePushNotificationClient
// (registered below as a named + scoped client). The former jeeb-specific
// PushController + IPushNotificationClient device-register passthrough was removed
// with the salehly mirror. InMemoryDeviceTokenStore is deliberately KEPT because
// the SEND path (PushNotificationService fan-out, consumed by KycService,
// ChatDispatcher, DisputeService, RatingRevealJob, PushAutoOfflineNotifier) still
// reads device tokens from it — that is a separate C-domain (push transport /
// retry / SLA) with no upstream owner yet. Do not delete this store until the
// push-transport service lands; deleting it now would break the send pipeline.
builder.Services.AddSingleton<IDeviceTokenStore, InMemoryDeviceTokenStore>();
builder.Services.AddSingleton<IPushRetryQueue, InMemoryPushRetryQueue>();
builder.Services.AddSingleton<InMemoryPushDeliveryTracker>();
builder.Services.AddSingleton<IPushDeliveryTracker>(sp => sp.GetRequiredService<InMemoryPushDeliveryTracker>());

var pushOpts = builder.Configuration.GetSection(PushOptions.SectionName).Get<PushOptions>() ?? new PushOptions();
if (pushOpts.UseFcmTransport)
{
    builder.Services.AddHttpClient<FcmPushTransport>();
    builder.Services.AddSingleton<IPushTransport, FcmPushTransport>();
    builder.Services.AddSingleton<IPushTransport>(_ => new InMemoryPushTransport(DevicePlatform.Apns));
}
else
{
    builder.Services.AddSingleton<IPushTransport>(_ => new InMemoryPushTransport(DevicePlatform.Fcm));
    builder.Services.AddSingleton<IPushTransport>(_ => new InMemoryPushTransport(DevicePlatform.Apns));
}

builder.Services.AddSingleton<IPushNotificationService, PushNotificationService>();
builder.Services.AddSingleton<PushRetryQueueProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PushRetryQueueProcessor>());

// Delivery requests — BR-9 concurrency cap enforcement at creation
// (T-backend-049). In-memory store for the MVP; production wiring will
// proxy to delivery-service via NSwag-generated client, backed by the
// schema in db/migrations/0004 with a SERIALIZABLE-isolation create or
// a partial unique index on (client_id) WHERE status in active-set.
builder.Services.AddSingleton<IRequestsStore, InMemoryRequestsStore>();

// Tier-existence probe consumed by RequestsController to enforce
// T-backend-007's "validate tier exists" criterion. Distinct interface
// from JeebGateway.Tiers.ITiersStore (the admin/catalog surface).
builder.Services.AddSingleton<JeebGateway.Requests.ITiersStore, JeebGateway.Requests.InMemoryTiersStore>();

// Delivery cancellation pipeline (T-backend-024 / JEEB-42).
//
// thin-BFF wire (T-thin-bff-ban): the Jeeber restriction record-of-truth is
// flag-gated. When FeatureFlags:UseUpstream:Ban is true the store proxies the
// real ban-service (Rust, port 10065) via BanServiceJeeberRestrictionStore →
// IBanServiceClient; when false it falls back to InMemoryJeeberRestrictionStore.
// CancellationService — invoked from BOTH AdminCancellationsController and
// DeliveriesController — consumes IJeeberRestrictionStore, so swapping the impl
// here gates both call sites with no controller branching. The in-memory store
// is deliberately KEPT as the flag-off fallback (do not delete in this PR).
var banFlags = builder.Configuration
    .GetSection(UpstreamFeatureFlags.SectionName)
    .Get<UpstreamFeatureFlags>() ?? new UpstreamFeatureFlags();
if (banFlags.Ban)
{
    builder.Services.AddSingleton<IJeeberRestrictionStore, BanServiceJeeberRestrictionStore>();
}
else
{
    builder.Services.AddSingleton<IJeeberRestrictionStore, InMemoryJeeberRestrictionStore>();
}
builder.Services.AddSingleton<ICancellationService, CancellationService>();

// Mutual-blind ratings (T-backend-020 / JEEB-38).
//
// Reveal logic is pure (BlindRevealPolicy): both parties' ratings stay
// blind until both sides submit OR the 7-day window closes (after which
// the row is locked as no-rating, with whatever exists already visible).
// The canonical rating row is captured by score-taking-service via the
// typed IScoreServiceClient registered in ServiceClientExtensions.
builder.Services.Configure<RatingOptions>(builder.Configuration.GetSection(RatingOptions.SectionName));
builder.Services.AddSingleton<IRatingStore, InMemoryRatingStore>();
builder.Services.AddSingleton<IRatingService, RatingService>();

// OTP handover verification + admin escalation (T-backend-015 / JEEB-33).
builder.Services.Configure<OtpHandoverOptions>(builder.Configuration.GetSection(OtpHandoverOptions.SectionName));
builder.Services.AddSingleton<IAdminEscalationStore, InMemoryAdminEscalationStore>();
builder.Services.AddSingleton<OtpHandoverSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtpHandoverSweeper>());

// T-BE-019 (JEB-55): shared cache for the external-OTP attempt counter
// and lockout flag. MVP wires AddDistributedMemoryCache() (single-process);
// production swaps to AddStackExchangeRedisCache() against the cluster's
// Redis so attempts cannot be circumvented by hitting different gateway
// replicas. The same IDistributedCache abstraction works for both.
builder.Services.AddDistributedMemoryCache();

// Geo-matching engine (T-backend-008) — RELOCATED to delivery-service.
// The gateway's in-memory geo-matching engine (great-circle distance scan +
// in-memory rating provider) was deleted; courier matching now lives in
// delivery-service (Go) behind POST /api/v1/matching/run. MatchingController
// is a thin BFF that delegates via IDeliveryServiceClient.RunMatchingAsync
// (registered with the standard pipeline in AddDownstreamClients).
// See DELIVERY-SERVICE-RELOCATION-DESIGN.md §2.1 + §5.

// Delivery tier catalog (T-backend-009).
// In-memory store seeded with the five default tiers (Urgent, Same-Day,
// Scheduled, Economy, On-the-Way); admins can CRUD via /admin/tiers and
// changes take effect on the next request because each List/Get returns
// a deep-cloned snapshot. Production wiring will hit Postgres via a
// follow-up migration colocated with delivery_requests.
builder.Services.AddSingleton<JeebGateway.Tiers.ITiersStore, JeebGateway.Tiers.InMemoryTiersStore>();

// Request expiry + no-offer nudge (T-backend-028).
// 10-min "try expanding tier" prompt and 30-min terminal expiry. The
// in-memory notifier records calls so integration tests can assert
// delivery; production swap proxies to notification-service via the
// BFF NSwag-generated client. The sweeper drives both windows from a
// single periodic scan against IRequestsStore.
builder.Services.Configure<RequestExpiryOptions>(builder.Configuration.GetSection(RequestExpiryOptions.SectionName));
builder.Services.AddSingleton<InMemoryRequestExpiryNotifier>();
builder.Services.AddSingleton<IRequestExpiryNotifier>(sp => sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
builder.Services.AddSingleton<RequestExpirySweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RequestExpirySweeper>());

// Scheduled delivery activator (T-backend-046, Phase 2).
// At ScheduledAt - MatchingBuffer the activator flips the row from
// 'scheduled' to 'pending' (kicking off matching) and pushes the
// "matching window opened" reminder to the Client. In-memory notifier
// records calls so integration tests can assert delivery; production
// wiring proxies to notification-service via the BFF NSwag client.
builder.Services.Configure<ScheduledDeliveryOptions>(builder.Configuration.GetSection(ScheduledDeliveryOptions.SectionName));
builder.Services.AddSingleton<InMemoryScheduledDeliveryNotifier>();
builder.Services.AddSingleton<IScheduledDeliveryNotifier>(sp => sp.GetRequiredService<InMemoryScheduledDeliveryNotifier>());
builder.Services.AddSingleton<ScheduledDeliveryActivator>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ScheduledDeliveryActivator>());

// Prohibited items catalog + per-user acknowledgment ledger (T-backend-027).
// In-memory store for the MVP; production wiring will hit Postgres directly
// using the schema in db/migrations/0005 (catalog) plus a follow-up migration
// for the acknowledgment ledger.
builder.Services.AddSingleton<IProhibitedItemsStore, InMemoryProhibitedItemsStore>();

// Prohibited-item NLP scanner + admin review queue (T-backend-048).
// The scanner runs Damerau-Levenshtein fuzzy matching with a synonym
// expansion pass against the active catalog. Matches above the review
// threshold are recorded in IFlaggedRequestStore for admin moderation;
// the scanner never auto-blocks. Stores are in-memory for the MVP; the
// flagged queue gets a Postgres-backed implementation alongside the
// admin_actions audit table in 0005.
builder.Services.AddSingleton<IProhibitedItemSynonymRegistry, InMemorySynonymRegistry>();
builder.Services.AddSingleton<IProhibitedItemScanner, ProhibitedItemScanner>();
builder.Services.AddSingleton<IFlaggedRequestStore, InMemoryFlaggedRequestStore>();

// Admin audit log (T-backend-030).
// In-memory append-only store for the MVP; production swap writes to
// db/migrations/0005.admin_actions on the same transaction as the
// mutation so the audit trail can never diverge from entity state.
builder.Services.AddSingleton<IAdminAuditLog, InMemoryAdminAuditLog>();

// Dispute reporting pipeline (T-backend-025 / JEEB-43).
//
// POST /deliveries/{id}/dispute files a dispute (one open at a time per
// delivery). GET /disputes lists the caller's filed disputes; GET /disputes/{id}
// returns a single dispute (filer-or-admin visibility). PUT /admin/disputes/{id}/resolve
// transitions the case through filed → under_review → resolved | dismissed
// and pushes the outcome to the filer.
//
// Photos are referenced by URL (already-uploaded via upload-service), the
// same pattern parcel photos use on DeliveryRequest.Photos. Push fan-out
// rides the unified IPushNotificationService pipeline.
//
// Production swap: InMemoryDisputeStore → Postgres-backed store colocated
// with admin moderation tables; the photo-URL set acquires upload-service
// signed-URL validation; resolution-text rendering moves to dynamic-template
// via the BFF NSwag client.
builder.Services.AddSingleton<IDisputeStore, InMemoryDisputeStore>();
builder.Services.AddSingleton<IDisputeService, DisputeService>();

// ---------------------------------------------------------------------------
// T-BE-028 / JEB-64: dispute case state machine + chat/GPS evidence
// orchestration. Additive over T-backend-025; adds the new v1 surface:
//   POST /v1/deliveries/{id}/escalate
//   POST /admin/v1/disputes/{id}/resolve
//
// Refund path proxies to olivium-dev/unified_payment_gateway (locked-in
// payments policy). InMemoryPaymentRefundClient stands in for tests /
// local dev; HttpPaymentRefundClient takes over when
// Services:UnifiedPayment:BaseUrl is configured.
//
// Evidence orchestrator captures chat transcript + GPS polyline at
// escalate time with per-call timeouts so the AC6 1s open budget holds
// even under upstream degradation (PO blocker #3).
// ---------------------------------------------------------------------------
builder.Services.Configure<DisputeEvidenceOptions>(
    builder.Configuration.GetSection(DisputeEvidenceOptions.SectionName));
builder.Services.AddSingleton<IDisputeCaseStore, InMemoryDisputeCaseStore>();
// Scoped: the dispute case service depends on the evidence orchestrator and is
// resolved per-request by DisputeCasesController, so both are scoped together.
// The orchestrator's deps (ILocationStore, IRequestsStore) are singletons (safe
// to inject into a scoped service). Chat-transcript capture was removed with the
// gateway chat BFF client (salehly mirror), so the orchestrator no longer holds a
// typed chat HttpClient.
builder.Services.AddScoped<IDisputeEvidenceOrchestrator, DisputeEvidenceOrchestrator>();
builder.Services.AddScoped<IDisputeCaseService, DisputeCaseService>();

builder.Services.AddSingleton<InMemoryPaymentRefundClient>();
var paymentBaseUrl = builder.Configuration["Services:UnifiedPayment:BaseUrl"]
    ?? builder.Configuration["Services:UnifiedPayment"];
if (!string.IsNullOrWhiteSpace(paymentBaseUrl))
{
    builder.Services.AddHttpClient<IPaymentRefundClient, HttpPaymentRefundClient>(http =>
    {
        http.BaseAddress = new Uri(paymentBaseUrl!.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(5);
    });
}
else
{
    builder.Services.AddSingleton<IPaymentRefundClient>(sp =>
        sp.GetRequiredService<InMemoryPaymentRefundClient>());
}

// Jeeber KYC submission pipeline (T-backend-004 / JEEB-22).
//
// POST /kyc/submit lands ID front/back + selfie in encrypted document
// storage, runs the liveness check stub, and pushes a queue entry with
// status 'pending_review' for the admin moderation pipeline. Production
// wiring swaps the in-memory document storage for S3 with per-object
// KMS data keys, the liveness stub for a real vendor (e.g. AWS Rekognition
// or iProov), and the in-memory KYC store for a Postgres-backed
// implementation colocated with admin_actions.
builder.Services.AddSingleton<IKycStore, InMemoryKycStore>();
builder.Services.AddSingleton<IKycDocumentStorage, InMemoryEncryptedDocumentStorage>();
builder.Services.AddSingleton<IKycLivenessChecker, StubKycLivenessChecker>();
builder.Services.AddSingleton<IKycService, KycService>();

// Users / profile / saved addresses / admin search (T-backend-029).
// In-memory store for the MVP; production wiring will proxy to auth-service
// via an NSwag-generated client, backed by the schema in 0001 + 0006.
builder.Services.AddSingleton<InMemoryUsersStore>();
builder.Services.AddSingleton<IUsersStore>(sp => sp.GetRequiredService<InMemoryUsersStore>());

// Dual-role identity + BR-1 enforcement (T-backend-041).
// Validates that a user cannot act as both Client and Jeeber simultaneously
// in the same delivery, and that role switches are gated on having no active
// deliveries under the current role.
builder.Services.AddSingleton<IDualRoleService, DualRoleService>();

// Account deletion lifecycle (T-backend-035, GDPR-like).
// In-memory store for the MVP; production wiring will be a worker that
// polls db/migrations/0010.account_deletions and proxies the financial
// anonymization step to unified_payment_gateway (locked-in payments
// policy). The 30-day SLA lives in InMemoryAccountDeletionStore.PurgeDelay.
builder.Services.AddSingleton<InMemoryFinancialLedger>();
builder.Services.AddSingleton<IFinancialLedgerAnonymizer>(sp => sp.GetRequiredService<InMemoryFinancialLedger>());
builder.Services.AddSingleton<InMemoryAccountDeletionStore>();
builder.Services.AddSingleton<IAccountDeletionStore>(sp => sp.GetRequiredService<InMemoryAccountDeletionStore>());

// Data-export pipeline (T-backend-042, GDPR-like right of access).
// POST /users/me/data-export queues a full export (profile, orders,
// ratings, chat history); a background processor packages the bytes,
// stamps a single-use download token, and notifies the user. The 72-hour
// SLA lives in DataExportOptions.Sla. Production wiring will swap the
// in-memory store/providers for the Postgres-backed worker and an NSwag
// notification-service client.
builder.Services.Configure<DataExportOptions>(builder.Configuration.GetSection(DataExportOptions.SectionName));
builder.Services.AddSingleton<IDataExportStore, InMemoryDataExportStore>();
builder.Services.AddSingleton<InMemoryDataExportRatingsProvider>();
builder.Services.AddSingleton<IDataExportRatingsProvider>(sp => sp.GetRequiredService<InMemoryDataExportRatingsProvider>());
// Chat history for GDPR export. The gateway no longer carries a chat BFF client
// (removed with the salehly mirror), so this provider returns an empty transcript
// and logs the documented per-user enumeration limitation pending a generic
// list-channels-for-member chat-service endpoint.
builder.Services.AddScoped<IDataExportChatHistoryProvider, ChatServiceDataExportChatHistoryProvider>();
builder.Services.AddSingleton<InMemoryDataExportNotifier>();
builder.Services.AddSingleton<IDataExportNotifier>(sp => sp.GetRequiredService<InMemoryDataExportNotifier>());
// Scoped (was singleton): the packager now depends on the scoped
// IDataExportChatHistoryProvider (client-backed). DataExportProcessor already
// resolves the packager from a per-job scope, so scoped is correct and avoids a
// captive dependency on the scoped chat provider.
builder.Services.AddScoped<IDataExportPackager, DataExportPackager>();
builder.Services.AddSingleton<DataExportProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DataExportProcessor>());

// JWT token rotation + revocation (T-backend-043).
// 15-min access tokens, 30-day single-use refresh tokens rotated on
// every use; revocation triggers on suspension, password change, and
// phone change. In-memory refresh-token store for MVP — Postgres-backed
// implementation lands with the follow-up migration.
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
// AddAuthorization() / AddRateLimiter() already TryAdd TimeProvider.System — use
// TryAdd here so the existing test fixtures that assert Single<TimeProvider> still hold.
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
builder.Services.AddSingleton<IUsersStoreAdapter, UsersStoreRolesAdapter>();
builder.Services.AddSingleton<ITokenService, TokenService>();

// ===========================================================================
// User-management integration — EXACT mirror of the salehly-gateway sibling.
//
// jeeb-gateway proxies all user-management traffic through the NSwag-generated
// ServiceUserManagementClient (Services/ServiceUserManagementClient.cs,
// namespace JeebGateway.service.ServiceUserManagement) exactly as
// salehly-gateway does. The client is a named IHttpClientFactory client bound
// to UserManagementServiceApi:BaseUrl, with a scoped typed-client instance that
// hands the named HttpClient to the generated constructor.
//
// Controllers/UserController.cs is the byte-faithful salehly UserController
// (routes under /api/User: check, all, register, login, token-login,
// user-id-login, logout, social, forgot, reset, profile{,/update}, device
// register/unregister, payment auth-token issue/validate, bulk email delete).
// ===========================================================================
builder.Services.AddHttpClient("ServiceUserManagementClient", client =>
{
    var apiUrl = builder.Configuration["UserManagementServiceApi:BaseUrl"];
    if (!string.IsNullOrEmpty(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});

builder.Services.AddScoped<JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceUserManagementClient");
    var baseUrl = builder.Configuration["UserManagementServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient(baseUrl, client);
});

// Jeeber availability toggle + auto-offline sweeper (T-backend-023).
// In-memory implementations stand in for the durable Postgres row, the
// Redis geo index, and the offer-service withdrawal hook described in
// db/JEEBER_LOCATION_DESIGN.md. Production swaps each behind the same
// interfaces.
builder.Services.Configure<AutoOfflineOptions>(builder.Configuration.GetSection(AutoOfflineOptions.SectionName));

// Admin ops-map zone grouping (T-backend-051). Boundaries are
// reloaded on config change via IOptionsMonitor so operators can
// re-shape coverage without redeploying the gateway.
builder.Services.Configure<ZoneOptions>(builder.Configuration.GetSection(ZoneOptions.SectionName));
builder.Services.AddSingleton<IGeoIndex, InMemoryGeoIndex>();

// Offer record-of-truth (T-backend-010). thin-BFF wire: when
// FeatureFlags:UseUpstream:Offer is true the offer ledger is the real
// offer-service (Elixir/Phoenix, host port 10063) proxied via
// UpstreamPendingOffersStore → IOfferServiceClient; when false (default in
// non-production) the legacy InMemoryPendingOffersStore is used. The in-memory
// store is KEPT registered either way so existing fixtures and the auto-offline
// sweeper / accept-lookup paths (which offer-service has no read route for yet)
// continue to resolve it directly; store deletion is a tracked fast-follow.
builder.Services.AddSingleton<InMemoryPendingOffersStore>();
builder.Services.AddSingleton<IPendingOffersStore>(sp =>
{
    var flags = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<UpstreamFeatureFlags>>().Value;
    if (flags.Offer)
    {
        return new UpstreamPendingOffersStore(
            sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>());
    }

    return sp.GetRequiredService<InMemoryPendingOffersStore>();
});
// Realtime "new offer" fan-out for T-backend-010. Stubbed in-memory for
// the MVP (records dispatched events so tests can assert delivery);
// production wiring will swap for a SignalR / realtime-service client
// behind the same IOfferRealtimeNotifier contract.
builder.Services.AddSingleton<InMemoryOfferRealtimeNotifier>();
builder.Services.AddSingleton<IOfferRealtimeNotifier>(sp => sp.GetRequiredService<InMemoryOfferRealtimeNotifier>());

// Auto-offline notifications flow through the shared push pipeline
// (T-backend-022, T-backend-023) so they obey the same transport and retry
// rules as any other trigger.
builder.Services.AddSingleton<IAutoOfflineNotifier, PushAutoOfflineNotifier>();
builder.Services.AddSingleton<IAvailabilityStore, InMemoryAvailabilityStore>();
builder.Services.AddHostedService<AutoOfflineSweeper>();

// GPS location streaming + SSE delivery tracking (T-backend-014).
// The store is an in-memory ConcurrentDictionary keyed by Jeeber id with
// a 5-min TTL — production swaps to Redis (SET ... EX 300) keyed on
// jeeber:{id}:position so multiple gateway replicas share the view. The
// SSE controller reads the store on a 5-second timer, flips the event
// name to "last-seen" when the latest fix is older than the configured
// stale threshold (default 2 min), and ends the stream when the delivery
// row reaches a terminal status.
builder.Services.Configure<TrackingOptions>(builder.Configuration.GetSection(TrackingOptions.SectionName));
builder.Services.AddSingleton<ILocationStore, InMemoryLocationStore>();

// Real-time chat (T-backend-012) — REMOVED.
// The jeeb-specific SignalR hub (/hubs/chat), ChatDispatcher, and in-memory
// presence tracker have been removed in favour of the salehly sibling mirror:
// ChatController is now a stateless passthrough REST shim over the generic
// chat-service via the NSwag ServiceChatClient (registered above). Real-time
// fan-out is a chat-service / realtime-communication-service concern, not a
// gateway one.

// Wave 2-3 backend services.
// T-backend-017: Weekly settlement batch processing.
builder.Services.Configure<JeebGateway.Financials.WeeklySettlementOptions>(
    builder.Configuration.GetSection(JeebGateway.Financials.WeeklySettlementOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Financials.ISettlementBatchStore, JeebGateway.Financials.InMemorySettlementBatchStore>();
builder.Services.AddHostedService<JeebGateway.Financials.WeeklySettlementBatch>();

// T-backend-018: Earnings aggregation API.
builder.Services.AddSingleton<JeebGateway.Financials.IEarningsAggregationService, JeebGateway.Financials.EarningsAggregationService>();

// T-backend-019: Earnings PDF statement generation.
builder.Services.AddSingleton<JeebGateway.Financials.IEarningsPdfGenerator, JeebGateway.Financials.SimpleEarningsPdfGenerator>();

// T-backend-033: Admin finance dashboard API.
builder.Services.AddSingleton<JeebGateway.Financials.IAdminFinanceDashboardService, JeebGateway.Financials.AdminFinanceDashboardService>();

// T-backend-021: 7-day rating reveal cron job.
builder.Services.Configure<JeebGateway.Ratings.RatingRevealOptions>(
    builder.Configuration.GetSection(JeebGateway.Ratings.RatingRevealOptions.SectionName));
builder.Services.AddHostedService<JeebGateway.Ratings.RatingRevealJob>();

// T-backend-040: Low-rating auto-flag and admin notification.
builder.Services.Configure<JeebGateway.Ratings.LowRatingFlagOptions>(
    builder.Configuration.GetSection(JeebGateway.Ratings.LowRatingFlagOptions.SectionName));
builder.Services.AddHostedService<JeebGateway.Ratings.LowRatingAutoFlag>();

// T-backend-037: Chat data retention is now a chat-service concern.
// The in-gateway retention sweeper + in-memory retention store have been DELETED:
// the gateway holds no chat record-of-truth, so it cannot (and must not) purge
// messages. Retention/TTL belongs to the owning chat-service.

// T-backend-044: Masked phone calls via Twilio proxy (Phase 2).
builder.Services.Configure<JeebGateway.Calls.MaskedCallOptions>(
    builder.Configuration.GetSection(JeebGateway.Calls.MaskedCallOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Calls.IMaskedCallService, JeebGateway.Calls.MaskedCallService>();

// Resilient Whisper integration (T-backend-036).
// Per-attempt 10s timeout enforced via linked CTS inside ResilientTranscriptionService;
// HttpClient.Timeout is set to Infinite so the service's cancellation policy is authoritative.
// Retry with exponential backoff (3 attempts, 1s/2s/4s), circuit breaker (5 failures),
// secondary fallback provider, and health check integration.
builder.Services.Configure<WhisperOptions>(builder.Configuration.GetSection(WhisperOptions.SectionName));
builder.Services.AddHttpClient<IWhisperClient, WhisperClient>((sp, http) =>
{
    var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhisperOptions>>().Value;
    http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
    http.Timeout = Timeout.InfiniteTimeSpan;
});
builder.Services.AddSingleton<IWhisperCircuitBreaker, WhisperCircuitBreaker>();
builder.Services.AddSingleton<IAudioStore, InMemoryAudioStore>();
builder.Services.AddSingleton<ITranscriptionFallbackQueue, InMemoryTranscriptionFallbackQueue>();
builder.Services.AddSingleton<IFallbackTranscriptionProvider, NoOpFallbackTranscriptionProvider>();
builder.Services.AddScoped<ITranscriptionService, ResilientTranscriptionService>();
builder.Services.AddHealthChecks()
    .AddCheck<WhisperHealthCheck>("whisper", tags: new[] { "ready" });

// ---------------------------------------------------------------------------
// jeeb-state-service durable rewire (ADR-001-rev2, Layer-2 R1–R8).
//
// The gateway stays STATELESS: every persisted row lives behind the
// NSwag-typed IJeebStateServiceClient. Behind a feature flag so local/CI runs
// (no live state-service) fall back to the legacy in-memory stores. A
// circuit-breaker (in AddJeebStateServiceClient) degrades gracefully on a
// state-service blip instead of cascading fleet-wide 500s.
// ---------------------------------------------------------------------------
var stateOptions = new JeebGateway.StateService.StateServiceOptions
{
    BaseUrl = builder.Configuration["JeebStateService:BaseUrl"]
              ?? builder.Configuration["Services:JeebState:BaseUrl"]
              ?? string.Empty,
    TimeoutSeconds = int.TryParse(builder.Configuration["JeebStateService:TimeoutSeconds"], out var ts) ? ts : 5,
    Enabled = !bool.TryParse(builder.Configuration["JeebStateService:Enabled"], out var en) || en
};
var stateServiceWired = stateOptions.Enabled && !string.IsNullOrWhiteSpace(stateOptions.BaseUrl);
if (stateServiceWired)
{
    builder.Services.AddSingleton(stateOptions);
    builder.Services.AddJeebStateServiceClient(stateOptions);

    // R1 — idempotency (full 1:1; GET-by-key ⇒ bounce-survivable).
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IIdempotencyStore,
        JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore>();

    // R8 — rate-limit + handover locks (keyed by bucket/lockKey ⇒ bounce-survivable).
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateRateLimitStore,
        JeebGateway.StateService.RateLimiting.StateServiceRateLimitStore>();
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateLockStore,
        JeebGateway.StateService.RateLimiting.StateServiceLockStore>();

    // R6 — strikes + cancellation counters; R7 — OTP-escalation (durable writes).
    builder.Services.AddSingleton<JeebGateway.StateService.Strikes.IStateStrikeWriter,
        JeebGateway.StateService.Strikes.StateServiceStrikeWriter>();

    // R2/R3/R4/R5 — durable write-through (writes land; see contract gap note).
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateRefreshFamilyWriter,
        JeebGateway.StateService.Durable.StateServiceRefreshFamilyWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateKycWriter,
        JeebGateway.StateService.Durable.StateServiceKycWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateRatingWriter,
        JeebGateway.StateService.Durable.StateServiceRatingWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateDisputeWriter,
        JeebGateway.StateService.Durable.StateServiceDisputeWriter>();

    // Add jeeb-state-service to the aggregate-health roster (now 18 checks).
    builder.Services.AddHealthChecks()
        .AddUrlGroup(
            new Uri(stateOptions.BaseUrl.TrimEnd('/') + "/health"),
            name: "jeeb-state-service",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            tags: new[] { "ready", "downstream" });
}

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------

var app = builder.Build();

// PR #32 review B2 — must run FIRST so every downstream middleware (rate
// limiter, OTP per-IP partition, auth-correlation logs) sees the real client
// IP from X-Forwarded-For instead of the LB's internal address.
app.UseForwardedHeaders();

// PR #32 review B2 — single-process rate limiter warning.
//
// The OTP-request rate limiter (IOtpRequestRateLimiter) is registered as a
// per-process ConcurrentDictionary in OtpSignInServiceCollectionExtensions.
// With N replicas the per-phone cap effectively becomes 3 × N / minute and
// the per-IP cap 10 × N / minute — both bypassable. Production MUST swap
// the limiter to a Redis-backed implementation (ZADD ts; ZREMRANGEBYSCORE 0
// (now-60s); ZCARD), gated by the GatewayRateLimit:RedisConnectionString
// config key.
//
// TODO(JEB-37 follow-up): Postgres- / Redis-backed IOtpRequestRateLimiter.
// Tracked in qa/t-be-001/ac-mapping.md AC-GatewayRateLimit.
{
    var rateLimitRedis = app.Configuration["GatewayRateLimit:RedisConnectionString"];
    if (!app.Environment.IsDevelopment()
        && !app.Environment.EnvironmentName.Equals("Testing", StringComparison.OrdinalIgnoreCase)
        && string.IsNullOrWhiteSpace(rateLimitRedis))
    {
        app.Logger.LogWarning(
            "OTP rate limiter is in-memory but environment is '{Env}' (non-Development). " +
            "With multiple replicas the per-phone / per-IP caps scale with replica count and are bypassable. " +
            "Set GatewayRateLimit:RedisConnectionString to enable the Redis-backed limiter " +
            "(PR #32 review B2 / AC-GatewayRateLimit).",
            app.Environment.EnvironmentName);
    }
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestValidationMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

// JEB-67 / T-BE-031 AC7 — Swagger UI is on in Development without auth,
// gated behind the "admin" role in Staging (so internal QA can browse the
// surface) and entirely disabled in Production. The staging gate is
// enforced by middleware that rejects any /swagger request that doesn't
// carry an authenticated principal with the "admin" role.
if (app.Environment.IsDevelopment()
    || string.Equals(app.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Jeeb Gateway v1"));
}
else if (string.Equals(app.Environment.EnvironmentName, "Staging", StringComparison.OrdinalIgnoreCase))
{
    app.UseWhen(
        ctx => ctx.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase),
        branch =>
        {
            branch.Use(async (ctx, next) =>
            {
                var user = ctx.User;
                if (user?.Identity?.IsAuthenticated != true || !user.IsInRole("admin"))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                await next();
            });
        });
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Jeeb Gateway v1"));
}

app.UseRouting();

// T-backend-050 — per-endpoint latency histogram. Registered immediately
// after UseRouting so context.GetEndpoint() resolves the matched route
// template (e.g. "/api/requests/{id}") instead of the raw URL path; this
// keeps the metric's `route` label cardinality bounded.
app.UseMiddleware<RequestLatencyMiddleware>();

// CORS must run after UseRouting so endpoint-specific CORS metadata applies,
// and before UseAuthentication so preflight requests are not rejected as 401.
var corsPolicyName = (builder.Configuration.GetSection(SecurityOptions.SectionName)
    .Get<SecurityOptions>() ?? new SecurityOptions()).Cors.PolicyName;
app.UseCors(corsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

// Rate limiter must run after authentication so the per-user partition can
// read the JWT sub claim.
app.UseRateLimiter();

// R1 — gateway-wide Idempotency-Key handler. Runs after auth (so the key is
// scoped to an authenticated principal context) and before MapControllers so a
// replay short-circuits the endpoint. Durability lives in jeeb-state-service,
// so the guarantee survives a stop-first gateway bounce. Only wired when the
// state-service is configured.
if (stateServiceWired)
{
    app.UseMiddleware<JeebGateway.StateService.Idempotency.IdempotencyMiddleware>();
}

app.MapControllers();

// T-backend-050 — Prometheus scrape endpoint. Returns the OpenMetrics
// snapshot for the configured MeterProvider (ASP.NET Core HTTP server,
// HttpClient, and the Jeeb-owned RequestLatencyMetrics histogram).
app.MapPrometheusScrapingEndpoint("/metrics");

// Health endpoints — three distinct surfaces.
//
//   /health/live   liveness only ("self" check). K8s liveness probe — restarts
//                  the pod when the process can no longer respond.
//   /health/ready  readiness only (all "ready"-tagged checks, including the
//                  downstream URL-group probes). K8s readiness probe — pulls
//                  the pod out of Service load balancing on degradation.
//   /health        LIVENESS alias. MUST NOT depend on downstreams. The swarm /
//                  external monitor hits /health as the primary liveness probe;
//                  if it gated on downstream readiness, a single undeployed or
//                  flapping upstream would 503 the gateway and (under a
//                  health-gated deploy) pull it out of rotation — which is
//                  exactly the production incident this PR fixes. Liveness is
//                  process-only: returns 200 whenever the process can answer.
//                  Use /health/ready for the aggregated downstream view.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // liveness: always 200 if process is up
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = AggregateHealthResponseWriter.WriteAsync,
});
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false, // liveness alias — never gate on downstreams
});
// /health/aggregate — the JEB-67 / T-BE-031 AC2 dashboard surface, moved OFF
// the /health liveness path. Runs every check and returns 200 when all Healthy
// or 503 with a JSON body naming each failing service. External monitoring and
// the jeeb-admin dashboard use this for a full red/green view; the swarm and
// external liveness probe use /health (and k8s uses /health/live), neither of
// which may ever 503 on a downstream — that overload was the production incident.
app.MapHealthChecks("/health/aggregate", new HealthCheckOptions
{
    Predicate = _ => true,
    ResponseWriter = AggregateHealthResponseWriter.WriteAsync,
});

app.Run();

// Required for WebApplicationFactory<Program> integration tests.
public partial class Program { }
