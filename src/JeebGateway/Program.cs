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
using JeebGateway.Observability;
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
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Logs;
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

// JWT bearer auth (H-B5 / S02 ADR-001-rev3 token-authority): the gateway accepts
// AND fully validates two issuers, each pinned to its own named scheme keyed on
// the token's `iss` claim — NOT a widened ValidIssuers/multi-key single scheme.
// Scheme-per-issuer prevents key confusion: every issuer maps to exactly one
// signing key and one (iss,aud) pair.
//
//   * "Bearer"         -> iss=jeeb-gateway / aud=jeeb-clients, gateway TokenService key.
//   * "UserManagement" -> iss=user-management / aud=user-management, UM re-issue key.
//
// A policy scheme is the default; its ForwardDefaultSelector peeks the unvalidated
// `iss` to FORWARD to the right validating scheme (selection only — the forwarded
// scheme still verifies signature + iss + aud + exp). Endpoints retain the existing
// UserIdentity helper which also accepts the edge-injected X-User-Id header for MVP
// / tests, so registering schemes here does NOT make the gateway reject untokened
// MVP traffic.
// The default scheme name ("Bearer") is taken by the issuer-routing POLICY scheme,
// so the gateway's own validating JwtBearer scheme is registered under a distinct
// name. [Authorize] still works because the default authorization policy below lists
// both validating schemes explicitly.
const string GatewayBearerScheme = "GatewayBearer";

var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();

// SEC-H2 (Leg-11): fail closed if the gateway would boot with a placeholder / dev / too-short
// signing key outside Development/Testing. Bakes no key — only asserts a real secret was injected.
JeebGateway.Tokens.JwtSigningKeyGuard.EnsureNotPlaceholder(jwt.SigningKey, builder.Environment, "Jwt:SigningKey");

var signingBytes = Encoding.UTF8.GetBytes(jwt.SigningKey);

// UM trust config (optional, no fail-closed: an absent UmJwt section is fine).
// SECURITY: the UM signing key comes from config/secret only — never a committed
// literal in the gateway. When unset it falls back to the gateway's own
// Jwt:SigningKey (operationally the same fleet secret today); supplying a distinct
// UmJwt:SigningKey lets UM rotate off the leaked fleet key with no code change.
var umJwt = builder.Configuration.GetSection(UmJwtOptions.SectionName).Get<UmJwtOptions>() ?? new UmJwtOptions();
var umSigningKey = string.IsNullOrWhiteSpace(umJwt.SigningKey) ? jwt.SigningKey : umJwt.SigningKey;

// SEC-H2: when UmJwt supplies its OWN key (not the blank fall-through to Jwt:SigningKey,
// which is already guarded above), it too must not be a placeholder in production.
if (!string.IsNullOrWhiteSpace(umJwt.SigningKey))
{
    JeebGateway.Tokens.JwtSigningKeyGuard.EnsureNotPlaceholder(umJwt.SigningKey, builder.Environment, "UmJwt:SigningKey");
}

var umSigningBytes = Encoding.UTF8.GetBytes(umSigningKey);

const string UmScheme = "UserManagement";

builder.Services
    // Default scheme is a policy scheme that routes by issuer to a validating scheme.
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddPolicyScheme(JwtBearerDefaults.AuthenticationScheme, JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            // Peek (do NOT trust) the bearer's `iss` to pick the validating scheme.
            // Any malformed/missing token falls through to the gateway scheme, which
            // rejects it — there is no accept-without-validation path here.
            var authHeader = context.Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                var rawToken = authHeader["Bearer ".Length..].Trim();
                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                // N7.2 — a JWT-SHAPED but malformed token (e.g. "garbage.invalid.token")
                // passes CanReadToken (3 segments) yet ReadJwtToken THROWS on the bad base64,
                // and that throw escapes the scheme selector as a raw 500. A token we cannot
                // peek is simply forwarded to the gateway scheme, which validates it and
                // rejects it as 401 — the auth pipeline owns the rejection, not this selector.
                try
                {
                    if (handler.CanReadToken(rawToken)
                        && string.Equals(handler.ReadJwtToken(rawToken).Issuer, umJwt.Issuer, StringComparison.Ordinal))
                    {
                        return UmScheme;
                    }
                }
                catch
                {
                    // unparseable token → fall through to the gateway scheme (→ 401)
                }
            }
            return GatewayBearerScheme;
        };
    })
    // Gateway-issued tokens: iss=jeeb-gateway / aud=jeeb-clients, gateway key.
    .AddJwtBearer(GatewayBearerScheme, options =>
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
    })
    // UM re-issued tokens (post-role-switch): iss=user-management / aud=user-management,
    // UM key. Full signature + iss + aud + exp validation — no blind accept.
    .AddJwtBearer(UmScheme, options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = umJwt.Issuer,
            ValidateAudience = true,
            ValidAudience = umJwt.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(umSigningBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "sub",
            RoleClaimType = "roles"
        };
    });

// ADR-004 (upgrade-not-switch): the default authorization policy accepts ONLY the
// gateway-issued session scheme (iss=jeeb-gateway / aud=jeeb-clients). A client-route
// session token has exactly one valid audience. A token with aud=user-management on a
// client route is therefore rejected (401) — this closes the E4b/N5/N7.3 contradiction
// that ADR-003's two-scheme policy created. The UmScheme AddJwtBearer registration above
// is left DORMANT (non-fail-closed, reversible) but is referenced by NO route and is no
// longer in the default policy. There is no role-switch ceremony; a KYC-upgraded user's
// next gateway-minted session token carries their full available_roles (incl. jeeber).
// IHttpContextAccessor is required by the FallbackPolicy handler below to read the
// edge-injected X-User-Id header. Safe to register unconditionally (TryAdd).
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    JeebGateway.Auth.GatewayAudienceHandler>();

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
            GatewayBearerScheme)
        .RequireAuthenticatedUser()
        .Build();

    // ADR-004 Directive 1 — apply the gateway-audience auth approach UNIFORMLY to
    // every route. The FallbackPolicy governs every endpoint that carries NO
    // authorization metadata (i.e. no [Authorize] and no [AllowAnonymous]); previously
    // such endpoints were silently anonymous. It requires an identified caller: either
    // a validated gateway-session bearer (aud=jeeb-clients) authenticated under the
    // GatewayBearer scheme, OR the trusted edge X-User-Id header (the admin/edge path
    // we must preserve). Endpoints public by design (token mint, OTP, /health*, swagger,
    // dev/seed) opt out with [AllowAnonymous]. Routes with explicit [Authorize] keep
    // running under the DefaultPolicy (GatewayBearer-only) so aud=user-management on a
    // client route is still 401 (E4b / N5) — the fallback never weakens those.
    options.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
        .AddAuthenticationSchemes(GatewayBearerScheme)
        .AddRequirements(new JeebGateway.Auth.GatewayAudienceRequirement())
        .Build();

    // ── ADR-005 Layer 2 (user-type capability authorization) ──────────────────────────
    // Register ONE named policy per capability from the authoritative cap->roles map. Each
    // policy runs under the SAME Layer-1 scheme (GatewayBearer), requires an authenticated
    // caller, then adds a CapabilityRequirement satisfied by CapabilityAuthorizationHandler
    // (reads roles only, canonicalizes opaque->canonical, intersects with the map; never
    // reads audience). DefaultPolicy / FallbackPolicy / schemes above are UNTOUCHED.
    //   Layer 1 failure (wrong/absent audience)        -> 401 (never reaches Layer 2).
    //   Layer 2 failure (valid caller, wrong user type) -> 403 (CapabilityForbiddenResultHandler).
    foreach (var capability in JeebGateway.Auth.Capabilities.CapabilityRolePolicy.All)
    {
        options.AddPolicy(
            JeebGateway.Auth.Capabilities.Capabilities.PolicyFor(capability),
            policy => policy
                .AddAuthenticationSchemes(GatewayBearerScheme)
                // Layer 1 identity check — accepts a validated GatewayBearer principal OR the trusted
                // edge X-User-Id header, IDENTICALLY to the ADR-004 FallbackPolicy. Using
                // GatewayAudienceRequirement (not bare RequireAuthenticatedUser()) is what preserves
                // the admin/edge X-User-Id + X-User-Roles path (ADR-005 §7, test T5): a header-only
                // edge caller has no authenticated principal, so RequireAuthenticatedUser() would 401
                // it and break the path the ADR mandates keeping. Layer 1 here -> 401 on failure.
                .AddRequirements(new JeebGateway.Auth.GatewayAudienceRequirement())
                // Layer 2 user-type capability check -> 403 on failure (CapabilityForbiddenResultHandler).
                .AddRequirements(new JeebGateway.Auth.Capabilities.CapabilityRequirement(capability)));
    }
});

// ADR-005 Layer 2 — handler (resolves+canonicalizes roles), RFC7807 403 result shaper, and the
// default-deny coverage guard. FINAL one-shot step: all ~46 controllers are annotated and the guard
// ENFORCES (CapabilityGuardOptions.Enforce defaults to true); an un-annotated action now fails startup.
// CapabilityGuard:Enforce=false remains an emergency operator override only.
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    JeebGateway.Auth.Capabilities.CapabilityAuthorizationHandler>();
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationMiddlewareResultHandler,
    JeebGateway.Auth.Capabilities.CapabilityForbiddenResultHandler>();
builder.Services.Configure<JeebGateway.Auth.Capabilities.CapabilityGuardOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.Capabilities.CapabilityGuardOptions.SectionName));
// Register the guard as a resolvable singleton, then run it as a hosted service that shares the
// SAME instance. The singleton registration lets tests resolve the concrete guard and assert its
// FindUncoveredActions() verdict directly (AddHostedService<T> alone only exposes IHostedService).
builder.Services.AddSingleton<JeebGateway.Auth.Capabilities.CapabilityCoverageGuard>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<JeebGateway.Auth.Capabilities.CapabilityCoverageGuard>());

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
    // Render [FromForm] IFormFile actions (e.g. POST /kyc/submit) as a
    // multipart/form-data request body instead of letting Swashbuckle throw
    // "[FromForm] attribute used with IFormFile" — which otherwise 500s the
    // /swagger/v1/swagger.json document the moment the admin-gated Swagger
    // surface is enabled. See MultipartFormFileOperationFilter.
    options.OperationFilter<JeebGateway.Security.MultipartFormFileOperationFilter>();
    // POST /v1/requests is intentionally served by TWO actions disambiguated at
    // runtime by content-type: JeebRequestsController.Create ([Consumes(application/json)])
    // and RequestVoiceController.SubmitVoice ([Consumes(multipart/form-data)]). Swashbuckle's
    // swagger-gen groups purely by method+path and throws SwaggerGeneratorException
    // ("Conflicting method/path combination") for such a pair, which 500s the
    // /swagger/v1/swagger.json document under the admin-gated Swagger surface. Resolve
    // by emitting the first action for the shared path — the runtime selection is
    // unaffected (content-type negotiation still routes each request correctly).
    options.ResolveConflictingActions(apiDescriptions => apiDescriptions.First());
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
// WS-01: gateway-owned CMS authoring plane (W4/W7a). Durable Postgres store
// (cms_surfaces + cms_surface_versions, migration 0032) when
// GatewayPostgres:ConnectionString is set (JEBV4-132, AUDIT-A IN-MEM-LIVE);
// in-memory fallback for dev/CI/test. The INpgsqlConnectionFactory the Postgres
// store depends on is registered later in this file inside the same
// GatewayPostgres block — DI resolution happens at container-build time, so the
// registration order here is irrelevant.
builder.Services.AddCmsAuthoringPlane(builder.Configuration["GatewayPostgres:ConnectionString"]);
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
// JEBV4-58 (PP-7) — was registered with ONLY a BaseAddress (default 100s
// HttpClient timeout, no retry, no breaker: a slow chat-service call froze
// the request for up to 100s). Sub-100s timeout + the standard
// retry/breaker/timeout pipeline via AttachResilienceOnly (resilience only —
// deliberately NOT AttachStandardPipeline; this salehly-mirror client carries
// no bearer/ServiceAuth chain, see ServiceClientExtensions.AttachResilienceOnly).
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient("ServiceChatClient", client =>
{
    var apiUrl = builder.Configuration["ChatServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));
builder.Services.AddScoped<JeebGateway.service.ServiceChat.ServiceChatClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceChatClient");
    var baseUrl = builder.Configuration["ChatServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceChat.ServiceChatClient(baseUrl, client);
});

// S08 (JEB-50/51/52/53) — the Jeeb CONVERSATION typed client. Same chat-service
// host as ServiceChatClient (ChatServiceApi:BaseUrl), but a distinct typed client
// over chat-service's NET-NEW conversation aggregate (create-or-get by
// correlation, structured/text append, viewer-filtered list, membership check).
// Hand-authored to the agreed contract (the BanServiceClient precedent) until the
// chat-service conversation aggregate ships and regenerate-clients.sh can target
// it. The BFF controller (JeebConversationsController) is the SOLE chat caller and
// holds no conversation state; chat-service owns the domain + the VisibilityFilter.
// Behind IJeebConversationClient so the controller is integration-testable with a
// fake. The typed HttpClient registration supplies BaseAddress; the live path is
// gated by FeatureFlags:UseUpstream:Chat (default off -> 503) until PR-1 ships.
builder.Services.AddHttpClient<JeebGateway.Conversations.Client.IJeebConversationClient,
                               JeebGateway.Conversations.Client.JeebConversationClient>(client =>
{
    var apiUrl = builder.Configuration["ChatServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
});

// S08 (D / H6,N2) — the realtime membership-ticket issuer. The /v1/realtime gate
// mints a short-lived signed ticket scoped to (conversation, viewer, role) after
// the chat-service membership check, so realtime-comunication-service can authorize
// the WS join without calling chat-service (no inter-service coupling). HS256 over
// the gateway's existing Jwt:SigningKey (the same secret the realtime Guardian
// pipeline verifies the session bearer with). Singleton — the key is read once.
builder.Services.AddSingleton<JeebGateway.Conversations.Realtime.IRealtimeTicketIssuer,
                              JeebGateway.Conversations.Realtime.RealtimeTicketIssuer>();

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
// JEBV4-58 (PP-7) — was registered with ONLY a BaseAddress (default 100s
// timeout, no retry, no breaker). All gateway calls on this client are reads
// (list/unread-count) or PATCH mark-read/unread (naturally idempotent), so
// the full standard resilience pipeline (retry+breaker+timeout) is safe here.
// AttachResilienceOnly, not AttachStandardPipeline: this salehly-mirror client
// carries no bearer/ServiceAuth chain by design (see that helper's remarks).
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient("ServiceNotificationClient", client =>
{
    var apiUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));
builder.Services.AddScoped<JeebGateway.service.ServiceNotification.ServiceNotificationClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceNotificationClient");
    var baseUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
    return new JeebGateway.service.ServiceNotification.ServiceNotificationClient(baseUrl, client);
});

// JEB-1486 cutover step (2) — keep the deprecated jeeb.* localization ALIVE.
// The de-leak relocated the Jeeb notification taxonomy into the gateway
// (JeebNotificationCatalog) and emptied notification-service's locale catalog, so
// the running shared service no longer localizes any jeeb.* topic on its own.
// JeebNotificationCatalogSeeder re-registers every catalog entry (8 jeeb.* keys,
// EN+AR) into the live notification-service via its GENERIC, opaque-key
// POST /templates/register endpoint at boot — restoring the deprecated jeeb.*
// alias during the deprecation window without putting any Jeeb literal back into
// the shared service (GR2). Idempotent (upstream upserts on key; safe on every
// deploy/restart) and resilient (seeds on a background task with bounded
// exponential-backoff retry; never blocks or crashes boot).
//
// Dedicated named client so the seeder carries the standard outbound pipeline:
// bearer-forwarding (a no-op at boot — there is no inbound request) + the
// X-Service-Auth caller signature. Bound to the same ServiceNotificationClient
// base the passthrough client uses, so both agree on the upstream host.
//
// Gated: only registers when the Notification upstream is in use
// (FeatureFlags:UseUpstream:Notification=true, i.e. production) AND the seeder is
// not explicitly disabled (FeatureFlags:NotificationCatalogSeeder:Enabled=false).
// This keeps pure-dev/test boots (no upstream configured) free of seed traffic.
var notificationUpstreamEnabled =
    bool.TryParse(builder.Configuration["FeatureFlags:UseUpstream:Notification"], out var nUp) && nUp;
var notificationSeederEnabled =
    !bool.TryParse(builder.Configuration["FeatureFlags:NotificationCatalogSeeder:Enabled"], out var nSeed)
    || nSeed;
if (notificationUpstreamEnabled && notificationSeederEnabled)
{
    var seederClient = builder.Services.AddHttpClient(
        JeebGateway.Notifications.JeebNotificationCatalogSeeder.HttpClientName,
        client =>
        {
            var apiUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(apiUrl))
            {
                // Trailing slash so the relative "templates/register" resolves
                // under the host rather than replacing the path.
                client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            }

            client.Timeout = TimeSpan.FromSeconds(30);
        });
    // Standard outbound auth chain (transient handlers registered in
    // AddDownstreamClients): forward any caller bearer + sign X-Service-Auth.
    seederClient.AddHttpMessageHandler<JeebGateway.Services.Bff.BearerForwardingHandler>();
    seederClient.AddHttpMessageHandler<JeebGateway.Services.Bff.ServiceAuthSigningHandler>();

    builder.Services.AddHostedService<JeebGateway.Notifications.JeebNotificationCatalogSeeder>();
}

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
// JEBV4-58 (PP-7) — was registered with ONLY a BaseAddress (default 100s
// timeout, no retry, no breaker). This client's writes are dispatch actions
// (device register, broadcast, send-to-user) with NO idempotency key in the
// generated client, so a retried 5xx/timeout could duplicate-deliver a push
// the upstream already sent — the exact non-idempotent-POST case PP-7 calls
// out. AttachBreakerAndTimeoutOnly gives it the sub-100s timeout + breaker
// (a genuinely down upstream still trips and fails fast) WITHOUT retrying the
// same dispatch call. Not AttachStandardPipeline either: no bearer/ServiceAuth
// chain by design (salehly mirror).
ServiceClientExtensions.AttachBreakerAndTimeoutOnly(builder.Services.AddHttpClient("ServicePushNotificationClient", client =>
{
    var apiUrl = builder.Configuration["PushNotificationServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));
builder.Services.AddScoped<JeebGateway.service.ServicePushNotification.ServicePushNotificationClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServicePushNotificationClient");
    var baseUrl = builder.Configuration["PushNotificationServiceApi:BaseUrl"];
    var pushClient = new JeebGateway.service.ServicePushNotification.ServicePushNotificationClient(baseUrl, client);
    // BUILD-NEWREQ-PUSH — forward the optional internal API key to the hand-written
    // topic seam (Send_notification_to_topicAsync sends X-Api-Key when non-empty).
    pushClient.InternalApiKey = builder.Configuration["PushNotificationServiceApi:InternalApiKey"];
    return pushClient;
});

// BUILD-CHAT-PUSH — the chat-message → push-notification trigger. Best-effort fan-out
// of an FCM push to the conversation's other delivery principal when a chat message is
// appended (the only missing link for real A→B chat push). Scoped because it composes
// the singleton IRequestsStore with the SCOPED ServicePushNotificationClient (:10040).
builder.Services.AddScoped<JeebGateway.Notifications.IChatMessagePushNotifier,
    JeebGateway.Notifications.ChatMessagePushNotifier>();

// BUILD-OFFER-PUSH — the offer-submitted → push-notification trigger. Best-effort FCM
// push to the request's CUSTOMER when a jeeber submits a bid (the second missing push
// link alongside chat). Scoped: composes the SCOPED ServicePushNotificationClient (:10040).
builder.Services.AddScoped<JeebGateway.Notifications.IOfferPushNotifier,
    JeebGateway.Notifications.OfferPushNotifier>();

// BUILD-NEWREQ-PUSH — the request-created → "finding jeebers" push trigger. Best-effort
// FCM topic broadcast to the jeeb_jeebers topic when a customer creates a delivery
// request (the third missing push link, after chat and offer). Scoped: composes the
// SCOPED ServicePushNotificationClient (:10040) via its hand-written topic seam.
builder.Services.AddScoped<JeebGateway.Notifications.INewRequestPushNotifier,
    JeebGateway.Notifications.NewRequestPushNotifier>();

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
// JEBV4-58 (PP-7) — was registered with ONLY a BaseAddress (default 100s
// timeout, no retry, no breaker). The only write is POST comment, which the
// upstream already guards with a documented 409 Conflict (duplicate-safe by
// contract), so the full standard resilience pipeline (retry+breaker+timeout)
// is safe. AttachResilienceOnly, not AttachStandardPipeline: no bearer/
// ServiceAuth chain by design (salehly mirror).
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient("ServiceFeedbackClient", client =>
{
    var apiUrl = builder.Configuration["FeedbackServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));
builder.Services.AddScoped<JeebGateway.service.ServiceFeedback.ServiceFeedbackClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceFeedbackClient");
    var baseUrl = builder.Configuration["FeedbackServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceFeedback.ServiceFeedbackClient(baseUrl, client);
});

// JEBV4-58 (PP-7) — was registered with ONLY a BaseAddress (default 100s
// timeout, no retry, no breaker). Catalog is read-only (ItemGETAsync via
// TechnicianReviewService), so the full standard resilience pipeline is safe.
// AttachResilienceOnly, not AttachStandardPipeline: no bearer/ServiceAuth
// chain by design (salehly mirror).
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient("ServiceCatalogClient", client =>
{
    var apiUrl = builder.Configuration["CatalogServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));
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

// S07 / BR-10 — delivery-service typed-client tunables (active-delivery cap).
// Bound from the existing Services:Delivery block (which holds the upstream
// BaseUrl) so the per-jeeber concurrent-active-delivery limit is config-driven;
// defaults to 2 (OffersController.ActiveDeliveriesLimit) when unset, preserving
// the historical BR-10 default with zero behaviour change.
builder.Services.Configure<JeebGateway.Services.DeliveryClientOptions>(
    builder.Configuration.GetSection(JeebGateway.Services.DeliveryClientOptions.SectionName));

// S06 / ADR-HB-001 — heart-beat presence cutover flag (FeatureFlags:Heartbeat).
// Bound here so AvailabilityController can resolve it via IOptions. Default false
// in EVERY environment this round (heart-beat not yet deployed); while off the
// availability surface keeps using the delivery-service presence wire. Flip via
// FeatureFlags__Heartbeat__Enabled=true (deploy workflow_dispatch), staging-first,
// after heart-beat is live and smoke-passed.
builder.Services.Configure<JeebGateway.Availability.HeartbeatFeatureOptions>(
    builder.Configuration.GetSection(JeebGateway.Availability.HeartbeatFeatureOptions.SectionName));

// JEB-1502: test control-plane options + job registry.
// The plane is fail-closed (Enabled=false) by default in every environment.
// The shared-secret header requirement provides a second gate when Enabled is true.
builder.Services.Configure<JeebGateway.TestControlPlane.TestControlPlaneOptions>(
    builder.Configuration.GetSection(JeebGateway.TestControlPlane.TestControlPlaneOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.TestControlPlane.ITestJobRegistry,
                              JeebGateway.TestControlPlane.TestJobRegistry>();

// Dev / test-harness endpoints flag (Features:DevEndpoints) — additive,
// fail-closed to 404. Bound here so the [DevOnly] action filter can resolve it
// via IOptionsMonitor. Defaults false and is committed false in EVERY
// appsettings (including Production); flipped on only via the env var
// Features__DevEndpoints__Enabled=true in the single environment that runs the
// external seeding harness. No auto-seed exists anywhere — see DevController.
builder.Services.Configure<JeebGateway.Security.DevEndpointOptions>(
    builder.Configuration.GetSection("Features").GetSection("DevEndpoints"));

// Swagger UI / OpenAPI flag (Features:Swagger) — additive, fail-closed to 404,
// admin-role-gated when on. Bound here so the request pipeline can read it via
// IConfiguration. Defaults false and is committed false in EVERY appsettings
// (including Production); flipped on only via the env var
// Features__Swagger__Enabled=true, applied exclusively by the deploy-to-jeeb.yml
// `swagger_ui` input. jeeb.fds-1.com is PUBLIC, so when ON under Production the
// surface is admin-gated (non-admin => 404), NOT the open Dev/Testing branch.
builder.Services.Configure<JeebGateway.Security.SwaggerOptions>(
    builder.Configuration.GetSection("Features").GetSection("Swagger"));

// Phone sign-in OTP orchestration options (Auth:Otp). Binds the Jeeb tenant's
// application id forwarded on every SendOTP/ValidateOTP to the shared
// one-time-password service, plus the contract ttlSeconds the gateway surfaces
// on request. The PRODUCTION AuthOtpController (/v1/auth/otp/*) routes through
// ServiceOTPClient -> one-time-password for send/validate and keeps ONLY the
// JWT/session mint in the gateway — the in-gateway OTP mock that duplicated
// send/validate business logic was retired in JEB-1516.
builder.Services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.OtpSignIn.OtpSignInOptions.SectionName));

// F-E (S02, JEB-37 / JEB-1422) — gateway-local phone admission policy + OTP-request
// burst guard, both evaluated in AuthOtpController BEFORE the one-time-password
// upstream is dialed (no upstream change). Region gate (LB-only -> invalid_country),
// E.164 parse (-> invalid_phone), and a per-IP AND per-phone sliding window
// (-> 429 rate_limited, SendOTP NOT called when throttled). Caps/region are
// configuration (Auth:Otp:Phone / Auth:Otp:RateLimit) so an env tunes them without
// a code change. The in-memory limiter is the M3 seam: bind a durable impl in prod.
builder.Services.Configure<JeebGateway.Auth.OtpSignIn.PhonePolicyOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.OtpSignIn.PhonePolicyOptions.SectionName));
builder.Services.Configure<JeebGateway.Auth.OtpSignIn.OtpRequestRateLimitOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.OtpSignIn.OtpRequestRateLimitOptions.SectionName));

// iter5 BATCHED-FIX — Super-Login+ demo roster for the debug picker
// (GET /api/User/demo-users). Roster + passcodes are config-only (env
// DemoUsers__Users__N__*), never hardcoded; the endpoint is anon by design
// (the picker precedes any session). See Auth/SuperLogin/DemoUsersOptions.cs.
builder.Services.Configure<JeebGateway.Auth.SuperLogin.DemoUsersOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.SuperLogin.DemoUsersOptions.SectionName));
// SECURITY-GATE — Super-Login+ prod-safety switch (SuperLogin:OpenMode). Default FALSE
// = prod-safe (token mint service-key gated, demo-user picker disabled). The MSI demo
// env sets SuperLogin__OpenMode=true to preserve the open demo behavior unchanged.
builder.Services.Configure<JeebGateway.Auth.SuperLogin.SuperLoginOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.SuperLogin.SuperLoginOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Auth.OtpSignIn.IPhonePolicy,
    JeebGateway.Auth.OtpSignIn.PhonePolicy>();
// Durability register #2 — OTP-request rate limiter. When
// GatewayRateLimit:RedisConnectionString is present, back the per-phone / per-IP caps
// with a Redis sorted-set limiter (RedisOtpRequestRateLimiter) so the window is shared
// across replicas and survives a restart (PR #32 review B2 / AC-GatewayRateLimit). The
// IConnectionMultiplexer singleton was removed with the salehly mirror, so it is
// (re)registered here lazily — only on this Redis-configured path. Absent the key
// (dev / CI / test), the in-process ConcurrentDictionary limiter is kept byte-for-byte.
var otpRateLimitRedisCs = builder.Configuration["GatewayRateLimit:RedisConnectionString"];
if (!string.IsNullOrWhiteSpace(otpRateLimitRedisCs))
{
    builder.Services.AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(
        _ => StackExchange.Redis.ConnectionMultiplexer.Connect(otpRateLimitRedisCs));
    builder.Services.AddSingleton<JeebGateway.Auth.OtpSignIn.IOtpRequestRateLimiter,
        JeebGateway.Auth.OtpSignIn.RedisOtpRequestRateLimiter>();
}
else
{
    builder.Services.AddSingleton<JeebGateway.Auth.OtpSignIn.IOtpRequestRateLimiter,
        JeebGateway.Auth.OtpSignIn.InMemoryOtpRequestRateLimiter>();
}

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
            // GW12-OBS-6 — business outcome counters for auth and durable writers.
            .AddMeter(BusinessOutcomeTelemetry.MeterName)
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

// GW12-OBS-1 (Leg-12) — trace-correlated, structured logs. Traces + metrics were
// already OTLP-exported, but logs were plain text with no trace/span id, so a log line
// could not be stitched to the trace it belongs to, and the X-Correlation-Id a client
// quotes was never written to any log (CorrelationIdMiddleware only echoed it on the
// wire). Wiring the OTel log exporter makes every log record automatically carry
// trace_id / span_id from Activity.Current, and IncludeScopes=true captures the
// X-Correlation-Id that CorrelationIdMiddleware now pushes as a log scope — so grepping
// the OTLP log backend for a reported correlation id finally returns the request's log
// lines. Same OTLP endpoint + service resource as the trace/metric exporters above, so
// this adds no new config surface and no new dependency (OpenTelemetry.Logs ships in the
// already-referenced OpenTelemetry core package). With no collector listening (dev/CI)
// the batching exporter drops silently — identical to the trace/metric exporters that
// already run in every environment.
builder.Logging.AddOpenTelemetry(logging =>
{
    logging.SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName));
    logging.IncludeScopes = true;
    logging.IncludeFormattedMessage = true;
    logging.ParseStateValues = true;
    logging.AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
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

// JEB-56: JeebPricingOptions — makes commission rates config-overridable.
// Defaults match CommissionCalculator constants (all tiers flat 10%).
builder.Services.Configure<JeebPricingOptions>(
    builder.Configuration.GetSection(JeebPricingOptions.SectionName));

// Cash settlement + receipt API (T-backend-016 / JEEB-34 → JEB-56).
//
// JEB-56: PostgresSettlementStore replaces InMemorySettlementStore when
// GatewayPostgres:ConnectionString is configured. The store is the durable
// COD settlement ledger (settlements table, migration 0015). When the
// connection string is absent (local dev / CI without Postgres), the in-memory
// fallback keeps the vertical exercisable.
//
// SettlementService re-computes the Jeeb fee (flat 10% commission,
// no insurance or floor) from the row's tier and posts a single
// best-effort ledger entry via ISettlementLedgerClient. The settlement row
// is the gateway-side system of record; the ledger post is idempotent on the
// settlement id. Cash settlement is a Jeeb product concern and keeps its own
// slim ledger contract in the Financials module — it does NOT ride on the
// wallet integration, which now mirrors the salehly-gateway sibling's
// upstream wallet API byte-for-byte (WalletController + ServiceWalletClient).
var gatewayPostgresCs = builder.Configuration["GatewayPostgres:ConnectionString"];
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Infrastructure.INpgsqlConnectionFactory>(
        _ => new JeebGateway.Infrastructure.NpgsqlConnectionFactory(gatewayPostgresCs));
    builder.Services.AddSingleton<ISettlementStore, PostgresSettlementStore>();

    // Durability register (JEBV4-124, AUDIT-A guard-gap) — pending-COD-settlement ENQUEUE
    // intent. MONEY-ADJACENT: the store's whole contract is idempotency ("no double-enqueue"),
    // so its in-memory ConcurrentDictionary (InMemorySettlementEnqueueStore) is a data-loss
    // hole — a restart drops the record of which deliveries were already enqueued and risks a
    // duplicate settlement enqueue. Postgres-backed (settlement_enqueue, migration 0034) with
    // DB-level idempotency (delivery_id PK + INSERT ON CONFLICT DO NOTHING, same as
    // PostgresSettlementStore) whenever GatewayPostgres is configured; guarded fail-closed in
    // prod-like envs (StoreDurabilityGuard.Critical). In-memory fallback for dev/CI/test only.
    builder.Services.AddSingleton<ISettlementEnqueueStore, PostgresSettlementEnqueueStore>();

    // Durability register: requests-durable [A] — the optional gateway-Postgres owner-list
    // mirror (delivery_requests, migration 0024). Registered ONLY here so DurableRequestsStore
    // resolves a non-null IDurableRequestsMirror in prod (see the [B] ctor arg below); absent
    // Postgres the mirror stays null and the durable owner-list degrades to the in-memory model.
    builder.Services.AddSingleton<JeebGateway.Requests.IDurableRequestsMirror,
        JeebGateway.Requests.PostgresDurableRequestsMirror>();

    // Durability register #11 — saved-locations. Registered BEFORE AddSavedLocations()
    // (whose TryAddSingleton InMemory fallback then no-ops), so the durable Postgres store
    // (saved_locations, migration 0016) wins whenever Postgres is configured.
    builder.Services.AddSingleton<JeebGateway.Users.SavedLocations.ISavedLocationStore,
        JeebGateway.Users.SavedLocations.PostgresSavedLocationStore>();
}
else
{
    builder.Services.AddSingleton<ISettlementStore, InMemorySettlementStore>();
    // JEBV4-124: in-memory settlement-enqueue fallback for dev/CI/test only. In a prod-like
    // env the fail-closed guard refuses this fallback (see StoreDurabilityGuard.Critical).
    builder.Services.AddSingleton<ISettlementEnqueueStore, InMemorySettlementEnqueueStore>();
}

// GR3 (JEB-1484) — the cash-settlement ledger post runs THROUGH UPG via the
// generic external-settlement endpoint (UpgSettlementLedgerClient ->
// IUpgSettlementClient) when FeatureFlags:UseUpstream:Payments is true. The flag
// defaults OFF, keeping today's in-process ledger (InMemorySettlementLedgerClient)
// as the instant rollback target. SettlementService treats the post as
// best-effort and is idempotent on the settlement id, so this swap is additive
// and non-breaking. Flip via FeatureFlags__UseUpstream__Payments=true once UPG's
// JEB-1484 PR is owner-approved + deployed (Services:UnifiedPayment:BaseUrl set).
if (builder.Configuration.GetValue<bool>("FeatureFlags:UseUpstream:Payments"))
{
    builder.Services.AddSingleton<ISettlementLedgerClient, UpgSettlementLedgerClient>();
}
else
{
    builder.Services.AddSingleton<ISettlementLedgerClient, InMemorySettlementLedgerClient>();
}

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
    var walletBuilder = builder.Services.AddHttpClient(name, client =>
    {
        var apiUrl = builder.Configuration[$"{configKey}:BaseUrl"];
        if (!string.IsNullOrEmpty(apiUrl))
        {
            client.BaseAddress = new Uri(apiUrl);
        }

        // JEBV4-58 (PP-7) — was ONLY a BaseAddress (default 100s timeout, no
        // retry, no breaker): the uncapped money-read ServiceWalletClient call
        // out. Sub-100s timeout below; see AttachBreakerAndTimeoutOnly call
        // for why retry is deliberately withheld on this client.
        client.Timeout = TimeSpan.FromSeconds(30);
    });

    // This same named client also backs WalletController's money-MUTATING
    // POSTs (holder/add, {holderId}/{walletId}/deactivate[/force-deactivate])
    // with no idempotency key in the generated client — a retried 5xx/timeout
    // after the upstream already applied the mutation risks a duplicate
    // credit/deactivation. AttachBreakerAndTimeoutOnly gives the client the
    // sub-100s timeout + circuit breaker (a genuinely failing wallet-service
    // still fails fast) WITHOUT retrying the same money-mutating request.
    // Not AttachStandardPipeline either: no bearer/ServiceAuth chain by design
    // (salehly mirror) — see AttachResilienceOnly's remarks.
    ServiceClientExtensions.AttachBreakerAndTimeoutOnly(walletBuilder);
}

ConfigureNamedClient("ServiceWalletClient", "WalletServiceApi");

// JEEBER-SPINE Defect 3 — dedicated named HttpClient for the Jeeb earnings BFF
// (JeebEarningsBffController). Bound to the SAME WalletServiceApi:BaseUrl as the generated
// wallet client; the BaseAddress is normalised with a trailing slash so the controller's
// relative "v1/wallet/jeeb/earnings" path resolves correctly. The caller's bearer is
// forwarded per-request inside the controller (own-scoped read).
//
// JEBV4-58 (PP-7) — the money-READ client the ticket calls out by name: was
// ONLY a BaseAddress (default 100s timeout, no retry, no breaker), so a slow
// (not down) wallet-service froze the earnings screen for up to 100s. Read-only
// (GET .../earnings[/export]) so the full standard resilience pipeline
// (retry+breaker+timeout via AttachResilienceOnly) is safe here — unlike the
// ServiceWalletClient above, this client never carries the money-mutating
// holder/add / deactivate POSTs.
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient(JeebGateway.Controllers.JeebEarningsBffController.WalletHttpClientName, client =>
{
    var apiUrl = builder.Configuration["WalletServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}));

builder.Services.AddScoped<JeebGateway.service.ServiceWallet.ServiceWalletClient>(sp =>
{
    var factory = sp.GetRequiredService<IHttpClientFactory>();
    var client = factory.CreateClient("ServiceWalletClient");
    var baseUrl = builder.Configuration["WalletServiceApi:BaseUrl"];
    return new JeebGateway.service.ServiceWallet.ServiceWalletClient(baseUrl, client);
});

// REALAPP fix — GET /v1/jeeb/wallet/ledger reads the holder's OWN transactions
// directly from the wallet DB (transactionheader + transactiondetails, joined via
// wallets.holderid), because the generic wallet-service exposes no ledger LIST
// endpoint. When WalletPostgres:ConnectionString is configured, use the Postgres
// reader; otherwise fall back to the empty-page reader (dev/CI/tests, no regression).
// Mirrors the GatewayPostgres direct-Postgres seam already used for COD settlements.
var walletPostgresCs = builder.Configuration["WalletPostgres:ConnectionString"];
if (!string.IsNullOrWhiteSpace(walletPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.JeebWallet.IJeebWalletLedgerReader>(sp =>
        new JeebGateway.JeebWallet.PostgresJeebWalletLedgerReader(
            walletPostgresCs!,
            sp.GetRequiredService<ILogger<JeebGateway.JeebWallet.PostgresJeebWalletLedgerReader>>()));
}
else
{
    builder.Services.AddSingleton<JeebGateway.JeebWallet.IJeebWalletLedgerReader,
        JeebGateway.JeebWallet.NullJeebWalletLedgerReader>();
}

// GW12-OBS-2 (Leg-12) — readiness depth for the gateway-owned Postgres databases.
// The durability Leg made 9+ stores depend on GatewayPostgres (and the wallet ledger
// reader on WalletPostgres), but nothing probed those databases, so /health/ready and
// /health/aggregate stayed green through a DB outage / pool exhaustion / credential
// rotation while every durable read/write threw. Register a SELECT-1 check per
// configured database, tagged "ready" (readiness only — a DB blip must not fail the
// liveness probe and pull the process out of rotation). Gated on the same
// !IsNullOrWhiteSpace(...) guard as the durable-store wiring, so dev/CI/test (no
// connection string) register no check and keep the existing green readiness surface.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddHealthChecks()
        .AddCheck(
            "gateway-postgres",
            new JeebGateway.Infrastructure.PostgresHealthCheck(gatewayPostgresCs!, "GatewayPostgres"),
            tags: new[] { "ready" });
}
if (!string.IsNullOrWhiteSpace(walletPostgresCs))
{
    builder.Services.AddHealthChecks()
        .AddCheck(
            "wallet-postgres",
            new JeebGateway.Infrastructure.PostgresHealthCheck(walletPostgresCs!, "WalletPostgres"),
            tags: new[] { "ready" });
}

// Notification preferences (T-backend-031 / JEB-1498).
// Wired to the generic remote-user-preferences service (Rust, :10067) so preferences
// survive restarts. Preferences are stored as an opaque JSON blob under key
// "jeeb.notification_prefs" — the shared service learns nothing about Jeeb topics (GR2).
// InMemoryNotificationPreferencesStore is kept as a fallback for local dev without the
// remote service (UseUpstream:RemoteUserPreferences=false).
if (builder.Configuration.GetValue("FeatureFlags:UseUpstream:RemoteUserPreferences", true))
{
    builder.Services.AddSingleton<INotificationPreferencesStore,
        RemoteUserPreferencesNotificationPreferencesStore>();
}
else
{
    builder.Services.AddSingleton<INotificationPreferencesStore, InMemoryNotificationPreferencesStore>();
}

// WS-02 — Saved Locations BFF (ACCT-04 / REQ-02). Net-new, gateway-thin, in-memory store.
builder.Services.AddSavedLocations();

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
// Durability register #10 — device tokens. Postgres-backed (device_tokens, migration 0017)
// when GatewayPostgres is configured so push fan-out targets survive a restart; the
// in-memory store is kept as the dev/CI/test fallback.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IDeviceTokenStore, JeebGateway.Push.PostgresDeviceTokenStore>();
}
else
{
    builder.Services.AddSingleton<IDeviceTokenStore, InMemoryDeviceTokenStore>();
}
// Durability register #12 — push-reliability trio (JEBV4-137 retry queue,
// JEBV4-136 delivery tracker, JEBV4-144 dispatch outbox below). All three used
// to live ONLY in gateway process memory, so every pending retry, delivery-log
// record and queued dispatch was silently DROPPED on each restart/replica move.
// Postgres-backed (push_retry_queue / push_delivery_tracker / notification_dispatch_outbox,
// migration 0030) whenever GatewayPostgres:ConnectionString is configured — the
// established FAIL-OPEN-then-gate pattern (StoreDurabilityGuard now enforces the
// Postgres impls in prod-like envs). The in-memory stores stay the dev/CI/test
// fallback when the connection string is absent.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IPushRetryQueue, PostgresPushRetryQueue>();
    builder.Services.AddSingleton<IPushDeliveryTracker, PostgresPushDeliveryTracker>();
}
else
{
    builder.Services.AddSingleton<IPushRetryQueue, InMemoryPushRetryQueue>();
    builder.Services.AddSingleton<InMemoryPushDeliveryTracker>();
    builder.Services.AddSingleton<IPushDeliveryTracker>(sp => sp.GetRequiredService<InMemoryPushDeliveryTracker>());
}

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

// JEB-1494: Gateway notification render→dispatch primitive.
// INotificationDispatchOutbox (JEBV4-144): Postgres-backed
// (notification_dispatch_outbox, migration 0030) whenever GatewayPostgres is
// configured so a queued-but-undelivered dispatch survives a restart; the
// in-memory store stays the dev/CI/test fallback.
// INotificationTemplateRenderer: static catalog; replace with an HTTP call to
// notification-service GET /render/{key} when that endpoint is live.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Services.Dispatch.INotificationDispatchOutbox,
                                   JeebGateway.Services.Dispatch.PostgresNotificationDispatchOutbox>();
}
else
{
    builder.Services.AddSingleton<JeebGateway.Services.Dispatch.INotificationDispatchOutbox,
                                   JeebGateway.Services.Dispatch.InMemoryNotificationDispatchOutbox>();
}
builder.Services.AddSingleton<JeebGateway.Services.Dispatch.INotificationTemplateRenderer,
                               JeebGateway.Services.Dispatch.StaticNotificationTemplateRenderer>();
builder.Services.AddScoped<JeebGateway.Services.Dispatch.IJeebNotificationDispatcher,
                            JeebGateway.Services.Dispatch.JeebNotificationDispatcher>();

// Delivery requests — BR-9 concurrency cap enforcement at creation
// (T-backend-049). In-memory store for the MVP; production wiring will
// proxy to delivery-service via NSwag-generated client, backed by the
// schema in db/migrations/0004 with a SERIALIZABLE-isolation create or
// a partial unique index on (client_id) WHERE status in active-set.
//
// SPINE-FOUNDATION / ADR-006: the create path becomes STATELESS behind
// FeatureFlags:DurableRequests (default OFF). When ON, DurableRequestsStore
// decorates the in-memory store — it mints ONE stable id, seeds the canonical
// delivery row (so POST /matching/run resolves instead of 404-ing) and records
// the saga in the state-service bundle ledger, while every non-create method
// delegates to the in-memory model. The in-memory store stays registered as
// the inner delegate AND as the flag-off path (the instant rollback lever — do
// NOT delete in this PR; retirement is a separate PR gated on S05–S15 green).
builder.Services.Configure<DurableRequestsOptions>(
    builder.Configuration.GetSection(DurableRequestsOptions.SectionName));

// JEB-50 (S05 H7): gateway-owned conversation auto-create on order create.
// The provisioner is ALWAYS registered (the durable store ctor depends on it),
// but it is a no-op that returns null unless FeatureFlags:ConversationAutoCreate
// :Enabled=true — so today's green create path is byte-for-byte unchanged until
// the flag is flipped. It is thin orchestration over the already-registered
// ServiceChatClient (chat-service POST /api/channels), holding no state.
builder.Services.Configure<JeebGateway.Conversations.ConversationProvisionOptions>(
    builder.Configuration.GetSection(JeebGateway.Conversations.ConversationProvisionOptions.SectionName));
// Singleton: the provisioner captures only IServiceScopeFactory (a singleton)
// and opens a fresh scope per call to resolve the SCOPED ServiceChatClient, so
// it is safe to inject into the singleton DurableRequestsStore.
builder.Services.AddSingleton<JeebGateway.Conversations.IConversationProvisioner,
                              JeebGateway.Conversations.ChatServiceConversationProvisioner>();

var durableRequests = builder.Configuration
    .GetSection(DurableRequestsOptions.SectionName)
    .Get<DurableRequestsOptions>() ?? new DurableRequestsOptions();

// The in-memory store is always registered (it is both the flag-off path and
// the inner delegate of the durable decorator).
builder.Services.AddSingleton<InMemoryRequestsStore>();

if (durableRequests.Enabled)
{
    // Saga bundle recorder — typed HttpClient over jeeb-state-service
    // POST /v1/state/bundles (the additive saga_bundles ledger). Base URL
    // resolved identically to the durable-rewire state options below so the
    // ledger and the typed JeebStateServiceClient hit the same service. A
    // standard resilience handler (retry + breaker + timeout) means a
    // state-service blip degrades the recorder to "Unavailable" (the create
    // still succeeds on the delivery row) instead of cascading a 500.
    var bundleBaseUrl = builder.Configuration["JeebStateService:BaseUrl"]
                        ?? builder.Configuration["Services:JeebState:BaseUrl"]
                        ?? string.Empty;
    builder.Services
        .AddHttpClient<JeebGateway.StateService.Durable.ISagaBundleRecorder,
                       JeebGateway.StateService.Durable.StateServiceSagaBundleRecorder>(http =>
        {
            if (!string.IsNullOrWhiteSpace(bundleBaseUrl))
            {
                http.BaseAddress = new Uri(bundleBaseUrl.TrimEnd('/') + "/");
            }
            http.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddStandardResilienceHandler();

    // JEB-50 (S05 H9b): broadcast-event recorder — typed HttpClient over the SAME
    // jeeb-state-service base URL + resilience pipeline as the saga recorder, but
    // targeting POST /v1/state/broadcasts (the additive append-only broadcast-log
    // bundler). When the conversation provisioner creates a broadcasting channel
    // for an order, DurableRequestsStore LOGS that broadcast event here so it is
    // durable and visible cross-service. Degrade-safe: a state-service blip trips
    // the breaker and the recorder reports Unavailable instead of failing create.
    builder.Services
        .AddHttpClient<JeebGateway.StateService.Durable.IBroadcastEventRecorder,
                       JeebGateway.StateService.Durable.StateServiceBroadcastEventRecorder>(http =>
        {
            if (!string.IsNullOrWhiteSpace(bundleBaseUrl))
            {
                http.BaseAddress = new Uri(bundleBaseUrl.TrimEnd('/') + "/");
            }
            http.Timeout = TimeSpan.FromSeconds(5);
        })
        .AddStandardResilienceHandler();

    builder.Services.AddSingleton<IRequestsStore>(sp => new DurableRequestsStore(
        sp.GetRequiredService<InMemoryRequestsStore>(),
        sp.GetRequiredService<JeebGateway.Services.Clients.IDeliveryServiceClient>(),
        sp.GetRequiredService<JeebGateway.StateService.Durable.ISagaBundleRecorder>(),
        sp.GetRequiredService<JeebGateway.Conversations.IConversationProvisioner>(),
        sp.GetRequiredService<JeebGateway.StateService.Durable.IBroadcastEventRecorder>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DurableRequestsOptions>>(),
        sp.GetRequiredService<ILogger<DurableRequestsStore>>(),
        // requests-durable [B] — supply the OPTIONAL 8th ctor arg so DurableRequestsStore's
        // _mirror is non-null in prod (registered inside the GatewayPostgres block, [A]).
        // GetService (not GetRequiredService): null when Postgres is not configured, which
        // degrades the durable owner-list to the in-memory snapshot (today's behaviour).
        sp.GetService<JeebGateway.Requests.IDurableRequestsMirror>()));
}
else
{
    builder.Services.AddSingleton<IRequestsStore>(sp => sp.GetRequiredService<InMemoryRequestsStore>());
}

// S06 (B1/B2/B3/ALT-2/ALT-3/ALT-4/ALT-4b/N5/N6): just-in-time delivery-row
// mirror for POST /matching/run. Registered AFTER IRequestsStore (it reads the
// request from whichever store the durable flag selected) and depends on the
// already-registered IDeliveryServiceClient (idempotent POST /api/v1/deliveries).
// Default-ON (MatchingMirrorOptions.Enabled) so a request that lives only in the
// gateway's in-memory store is seeded into delivery-service right before the run
// — closing the matching/run 404 without arming the heavier DurableRequests
// spine. Thin BFF orchestration only; instant rollback via
// FeatureFlags__MatchingMirror__Enabled=false. Scoped to match the controller's
// request lifetime; its deps (IRequestsStore, IDeliveryServiceClient) are
// resolvable in request scope.
builder.Services.Configure<JeebGateway.Matching.MatchingMirrorOptions>(
    builder.Configuration.GetSection(JeebGateway.Matching.MatchingMirrorOptions.SectionName));
builder.Services.AddScoped<JeebGateway.Matching.IDeliveryRowMirror,
                           JeebGateway.Matching.DeliveryRowMirror>();

// Tier-existence probe consumed by the request-create surfaces to enforce
// T-backend-007's "validate tier exists" criterion. feat/tier-unify-names:
// now a thin view over the SINGLE tier source of truth (the catalog at
// JeebGateway.Tiers.ITiersStore, registered below) — legacy codes
// (flash/express/standard/on_the_way/eco) stay accepted via the
// LegacyTierCodes alias table.
builder.Services.AddSingleton<JeebGateway.Requests.ITiersStore, JeebGateway.Requests.CatalogBackedTiersStore>();

// JEB-1507: CancellationPolicy thresholds (WeeklyThreshold, StrikeThreshold,
// RestrictionDurationHours) are configurable via appsettings so they can be
// adjusted per environment without a redeploy.
builder.Services.Configure<JeebGateway.Requests.Cancellation.CancellationPolicyOptions>(
    builder.Configuration.GetSection(
        JeebGateway.Requests.Cancellation.CancellationPolicyOptions.SectionName));

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
// blind until both sides submit. If the 7-day window closes first, the
// row is locked as no-rating without revealing one-sided ratings.
// The mutual-blind pairing store is the record-of-truth. Default is in-memory;
// when FeatureFlags:UseUpstream:Ratings is ON the store is swapped for
// FeedbackServiceRatingStore (persists/reads via the NSwag ServiceFeedbackClient).
// score-taking-service was removed entirely (owner directive). BanService precedent.
builder.Services.Configure<RatingOptions>(builder.Configuration.GetSection(RatingOptions.SectionName));
if (builder.Configuration.GetValue<bool>("FeatureFlags:UseUpstream:Ratings"))
{
    // Fail-fast wiring guard (JEB E2E 5.6/5.7). When the Ratings flag is ON the
    // delivery-ratings record-of-truth IS feedback-service: every POST
    // /api/deliveries/{id}/rate round-trips to FeedbackServiceApi:BaseUrl via the
    // ServiceFeedbackClient blind-rating surface (POST /ratings). The committed
    // appsettings default for that key is a 5000-series DEV PLACEHOLDER
    // (http://localhost:5011) that no service ever binds — every environment is
    // expected to override it (e.g. FeedbackServiceApi__BaseUrl=http://localhost:10064
    // locally, the swarm host in prod). If the flag is flipped ON but the override
    // is dropped, the client dials the dead placeholder, the connection is refused,
    // and EVERY rating surfaces as an opaque 502 — indistinguishable from a real
    // upstream/contract fault and exactly the misconfiguration that broke E2E
    // 5.6/5.7. Refuse to start in that state so the misconfig is loud at boot
    // instead of silent at request time.
    var feedbackBaseUrl = builder.Configuration["FeedbackServiceApi:BaseUrl"];
    if (string.IsNullOrWhiteSpace(feedbackBaseUrl)
        || feedbackBaseUrl.Contains("localhost:5011", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            "FeatureFlags:UseUpstream:Ratings is ON, which makes feedback-service the delivery-ratings " +
            "record-of-truth, but FeedbackServiceApi:BaseUrl is unset or still the dead dev placeholder " +
            $"('{feedbackBaseUrl ?? "<null>"}'). Set FeedbackServiceApi__BaseUrl to the reachable " +
            "feedback-service URL (e.g. http://localhost:10064 locally) or turn the flag OFF to use the " +
            "in-memory rating store. Left unset, every POST /api/deliveries/{id}/rate would 502 on a " +
            "refused connection to the placeholder host.");
    }

    builder.Services.AddSingleton<JeebGateway.Ratings.FeedbackServiceRatingStore>();
    builder.Services.AddSingleton<IRatingStore>(
        sp => sp.GetRequiredService<JeebGateway.Ratings.FeedbackServiceRatingStore>());
    // Fail-closed honesty guard: feedback-service currently exposes submit/reveal
    // only, not the list-expired-windows + mark-revealed/closed operations needed
    // for the gateway-owned 7-day sweep. Register an explicit extended adapter so
    // RatingRevealJob does not silently skip the upstream path.
    builder.Services.AddSingleton<IRatingStoreExtended, JeebGateway.Ratings.UnsupportedUpstreamRatingStoreExtended>();
}
else
{
    builder.Services.AddSingleton<InMemoryRatingStore>();
    builder.Services.AddSingleton<IRatingStore>(sp => sp.GetRequiredService<InMemoryRatingStore>());
    builder.Services.AddSingleton<IRatingStoreExtended>(sp => sp.GetRequiredService<InMemoryRatingStore>());
}
builder.Services.AddSingleton<IRatingService, RatingService>();

// OTP handover verification + admin escalation (T-backend-015 / JEEB-33).
builder.Services.Configure<OtpHandoverOptions>(builder.Configuration.GetSection(OtpHandoverOptions.SectionName));
// Durability register #5 — admin escalations. Postgres-backed (admin_escalations,
// migration 0021) when GatewayPostgres is configured so the unbounded escalation list
// survives a restart; in-memory fallback for dev/CI/test.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IAdminEscalationStore,
        JeebGateway.Requests.OtpHandover.PostgresAdminEscalationStore>();
}
else
{
    builder.Services.AddSingleton<IAdminEscalationStore, InMemoryAdminEscalationStore>();
}
builder.Services.AddSingleton<OtpHandoverSweeper>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OtpHandoverSweeper>());

// T-BE-019 (JEB-55): shared cache for the external-OTP attempt counter
// and lockout flag. MVP wires AddDistributedMemoryCache() (single-process);
// production swaps to AddStackExchangeRedisCache() against the cluster's
// Redis so attempts cannot be circumvented by hitting different gateway
// replicas. The same IDistributedCache abstraction works for both.
// Durability register #1 — shared cache. When Redis:ConnectionString is present, back
// IDistributedCache with the cluster's Redis (AddStackExchangeRedisCache) so the
// external-OTP attempt/lockout counters AND the in-app handover code (Gap G4) survive a
// gateway restart and cannot be circumvented across replicas. Absent the key (dev / CI /
// test), the in-process AddDistributedMemoryCache() is kept — same IDistributedCache
// abstraction, identical behaviour.
var redisCacheCs = builder.Configuration["Redis:ConnectionString"];
if (!string.IsNullOrWhiteSpace(redisCacheCs))
{
    builder.Services.AddStackExchangeRedisCache(o => o.Configuration = redisCacheCs);
}
else
{
    builder.Services.AddDistributedMemoryCache();
}

// Gap G4 (run-24 CHECK C): in-app delivery handover code. Minted at offer-accept
// and returned owner-scoped as `handoverCode`, held cross-replica-safe in the
// IDistributedCache above (same abstraction as the OTP attempt/lockout markers),
// and matched at handover via verify-precedence. Singleton (stateless over the
// singleton cache). See IHandoverCodeStore.
builder.Services.AddSingleton<JeebGateway.Requests.OtpHandover.IHandoverCodeStore,
    JeebGateway.Requests.OtpHandover.DistributedCacheHandoverCodeStore>();

// Geo-matching engine (T-backend-008) — RELOCATED to delivery-service.
// The gateway's in-memory geo-matching engine (great-circle distance scan +
// in-memory rating provider) was deleted; courier matching now lives in
// delivery-service (Go) behind POST /api/v1/matching/run. MatchingController
// is a thin BFF that delegates via IDeliveryServiceClient.RunMatchingAsync
// (registered with the standard pipeline in AddDownstreamClients).
// See DELIVERY-SERVICE-RELOCATION-DESIGN.md §2.1 + §5.

// Delivery tier catalog (T-backend-009).
// Admins CRUD via /admin/tiers and changes take effect on the next request
// (each List/Get reads fresh). Three default tiers (Urgent, Same-Day,
// Scheduled) are seeded either by migration 0029 + 0036 (Postgres path) or the
// in-memory store's constructor (dev/CI fallback).
//
// Durability register (JEBV4-125, AUDIT-A IN-MEM-LIVE) — the admin tier catalog
// used to live ONLY in gateway process memory, so an admin's tier edits reverted
// to the seeded defaults on every restart/replica move. PostgresTiersStore
// (tiers table, migration 0029) is the durable system of record whenever
// GatewayPostgres:ConnectionString is configured; the in-memory store stays the
// dev/CI/test fallback when it is absent — the established FAIL-OPEN-then-gate
// pattern (StoreDurabilityGuard now enforces the Postgres store in prod-like envs).
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Tiers.ITiersStore, JeebGateway.Tiers.PostgresTiersStore>();
}
else
{
    builder.Services.AddSingleton<JeebGateway.Tiers.ITiersStore, JeebGateway.Tiers.InMemoryTiersStore>();
}

// Request expiry + no-offer nudge (T-backend-028).
// 10-min "try expanding tier" prompt and per-tier terminal expiry. The
// in-memory notifier records calls so integration tests can assert delivery;
// production swap proxies to notification-service via the BFF NSwag-generated
// client. The sweeper drives both windows from a single periodic scan against
// IRequestsStore.
builder.Services.Configure<RequestExpiryOptions>(builder.Configuration.GetSection(RequestExpiryOptions.SectionName));
builder.Services.AddSingleton<InMemoryRequestExpiryNotifier>();
builder.Services.AddSingleton<IRequestExpiryNotifier>(sp => sp.GetRequiredService<InMemoryRequestExpiryNotifier>());
// JEB-1508: default to the no-op recorder; overridden by the durable
// StateServiceRequestExpiryRecorder below when stateServiceWired == true.
builder.Services.AddSingleton<IRequestExpiryRecorder>(NoOpRequestExpiryRecorder.Instance);
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
// Durability register #12 — prohibited items + acks. Postgres-backed (prohibited_items
// migration 0005 + severity/acks migration 0018) when GatewayPostgres is configured so
// admin edits and per-user acknowledgements survive a restart; in-memory fallback otherwise.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IProhibitedItemsStore,
        JeebGateway.ProhibitedItems.PostgresProhibitedItemsStore>();
}
else
{
    builder.Services.AddSingleton<IProhibitedItemsStore, InMemoryProhibitedItemsStore>();
}

// Prohibited-item NLP scanner + admin review queue (T-backend-048).
// The scanner runs Damerau-Levenshtein fuzzy matching with a synonym
// expansion pass against the active catalog. Matches above the review
// threshold are recorded in IFlaggedRequestStore for admin moderation;
// the scanner never auto-blocks. Stores are in-memory for the MVP; the
// flagged queue gets a Postgres-backed implementation alongside the
// admin_actions audit table in 0005.
builder.Services.AddSingleton<IProhibitedItemSynonymRegistry, InMemorySynonymRegistry>();
builder.Services.AddSingleton<IProhibitedItemScanner, ProhibitedItemScanner>();
// Durability register #13 — flagged requests. Postgres-backed (flagged_requests,
// migration 0019) when GatewayPostgres is configured so moderation queue entries survive
// a restart; in-memory fallback for dev/CI/test.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IFlaggedRequestStore,
        JeebGateway.ProhibitedItems.FlaggedRequests.PostgresFlaggedRequestStore>();
}
else
{
    builder.Services.AddSingleton<IFlaggedRequestStore, InMemoryFlaggedRequestStore>();
}

// JEB-63 (S05 N1 / A1.1): gateway-owned create-time prohibited-items moderation
// gate flag (default ON, INDEPENDENT of FeatureFlags:DurableRequests). When ON,
// RequestsController.Create runs the scanner before persisting and hard-rejects
// block-severity / soft-rejects warn-severity items. The lexicon stays
// gateway-owned (N11) — no ban-service coupling. The gate runs whether or not
// the durable saga create path is active (the two flags are independent). To
// disable explicitly set FeatureFlags__CreateModeration__Enabled=false.
builder.Services.Configure<JeebGateway.Requests.CreateModerationOptions>(
    builder.Configuration.GetSection(JeebGateway.Requests.CreateModerationOptions.SectionName));

// JEBV4-212 (E17): the shared create-time moderation evaluator. Both the legacy
// RequestsController.Create and the V1 JeebRequestsController.Create (the route the
// mobile app uses) route through this one gate so prohibited-items screening is
// enforced identically on BOTH create paths and can never drift. Singleton: all its
// deps (scanner, store, options) are singletons and it holds no per-request state.
builder.Services.AddSingleton<JeebGateway.Requests.CreateModerationEvaluator>();

// When the moderation gate is ON, seed a minimal default lexicon so the live
// gate has terms to match (the gate is inert against an empty lexicon). Default
// is ON: the seeder registers UNLESS the flag is explicitly false, mirroring
// CreateModerationOptions.Enabled's default-true (absence of the key = ON).
// Hosted so it runs once the singleton store is built. Additive + idempotent
// (skips if any item already exists, so an admin-seeded lexicon is preserved).
var createModerationEnabled =
    !bool.TryParse(
        builder.Configuration[$"{JeebGateway.Requests.CreateModerationOptions.SectionName}:Enabled"],
        out var cmEnabled)
    || cmEnabled;
if (createModerationEnabled)
{
    builder.Services.AddHostedService<JeebGateway.ProhibitedItems.DefaultLexiconSeeder>();
}

// Admin audit log (T-backend-030).
// In-memory append-only store for the MVP; production swap writes to
// db/migrations/0005.admin_actions on the same transaction as the
// mutation so the audit trail can never diverge from entity state.
// Durability register #14 — admin audit log. Postgres-backed (admin_actions, migration
// 0005) when GatewayPostgres is configured so the append-only admin action trail survives
// a restart; in-memory fallback for dev/CI/test.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IAdminAuditLog, JeebGateway.Admin.PostgresAdminAuditLog>();
}
else
{
    builder.Services.AddSingleton<IAdminAuditLog, InMemoryAdminAuditLog>();
}

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
    })
        // R6 (sprint-009 money-resilience-audit): front UPG refund with the org-standard
        // circuit breaker so a sustained UPG outage fails fast instead of eating the full
        // timeout on every dispute-resolve. Retry is double-refund-safe — the refund carries
        // an Idempotency-Key (dispute:{caseId}:refund), so UPG dedupes replays.
        .AddStandardResilienceHandler();
}
else
{
    builder.Services.AddSingleton<IPaymentRefundClient>(sp =>
        sp.GetRequiredService<InMemoryPaymentRefundClient>());
}

// S10 COD-compose: unified_payment_gateway COD + admin-batch client (JEB-56/57/62).
// Payments-only-via-UPG: the gateway RECORDS an intent / READS / FRONTS the admin
// mark-paid against UPG's live routes — it never touches a provider. Credentials
// (UPG :api X-Api-Key + AdminAuthPlug bearer) are env-injected, never committed.
// Live HttpClient when Services:UnifiedPayment:BaseUrl is set; in-memory fallback
// (idempotent on delivery id) keeps the compose surface exercisable in dev/test.
builder.Services.Configure<JeebGateway.Financials.Cod.UnifiedPaymentCodOptions>(
    builder.Configuration.GetSection(JeebGateway.Financials.Cod.UnifiedPaymentCodOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Financials.Cod.InMemoryUnifiedPaymentCodClient>();
if (!string.IsNullOrWhiteSpace(paymentBaseUrl))
{
    builder.Services
        .AddHttpClient<JeebGateway.Financials.Cod.IUnifiedPaymentCodClient,
                       JeebGateway.Financials.Cod.HttpUnifiedPaymentCodClient>(http =>
        {
            http.BaseAddress = new Uri(paymentBaseUrl!.TrimEnd('/') + "/");
            http.Timeout = TimeSpan.FromSeconds(10);
        })
            // R6 (sprint-009 money-resilience-audit): front UPG COD-record/mark-paid with the
            // org-standard circuit breaker so a slow UPG fails fast rather than degrading every
            // COD-record call for the full timeout. Retry is safe — the COD record carries an
            // Idempotency-Key (delivery_id), so UPG dedupes replays.
            .AddStandardResilienceHandler();
}
else
{
    builder.Services.AddSingleton<JeebGateway.Financials.Cod.IUnifiedPaymentCodClient>(sp =>
        sp.GetRequiredService<JeebGateway.Financials.Cod.InMemoryUnifiedPaymentCodClient>());
}

// Jeeber KYC submission pipeline (T-backend-004 / JEEB-22).
//
// S03 / ADR-0004 — the thin KYC BFF seam onto the KYC DOMAIN. Routes the JSON
// submit / ToS-stamp / status / queue / review flow to the OWNING kyc-service
// (live at :10074) via IKycServiceClient when FeatureFlags:UseUpstream:Kyc is ON.
// The BFF controllers (KycSubmissionBffController, KycStatusBffController,
// AdminKycController) compose this seam with contract-signing / cdn /
// user-management and hold ZERO KYC state. The legacy in-gateway KYC domain
// (InMemoryKycStore + the document/liveness fakes + the in-gateway KycService
// role-grant) and the interim ref store have been DELETED (ARCH LAW / guardrail
// #3): there is no in-memory fallback to serve a false-PASS — the seam fails
// closed (503) when the flag is off.
builder.Services.AddSingleton<IKycBffSeam, KycBffSeam>();

// Users / profile / saved addresses / admin search (T-backend-029).
// In-memory store for the MVP; production wiring will proxy to auth-service
// via an NSwag-generated client, backed by the schema in 0001 + 0006.
builder.Services.AddSingleton<InMemoryUsersStore>();
// Durability register #8 — users-durable. When GatewayPostgres is configured, IUsersStore
// resolves to UpstreamBackedUsersStore: admin user-search + the token-mint active_role read
// are served from a durable Postgres projection (users table, migration 0025) hydrated from
// user-management, and no longer evaporate on a bounce. Identity remains UM's source of
// truth (Postgres is a read-model projection). The in-process InMemoryUsersStore is kept as
// the permissive inner store (saved addresses / non-UUID OTP fallback). Absent Postgres,
// the in-memory store IS IUsersStore exactly as before.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Users.IUserProjectionStore,
        JeebGateway.Users.PostgresUserProjectionStore>();
    builder.Services.AddSingleton<JeebGateway.Users.IUpstreamUserProfileClient,
        JeebGateway.Users.ScopedUserManagementProfileClient>();
    builder.Services.AddSingleton<IUsersStore, JeebGateway.Users.UpstreamBackedUsersStore>();
}
else
{
    builder.Services.AddSingleton<IUsersStore>(sp => sp.GetRequiredService<InMemoryUsersStore>());
}

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
// Financial-ledger anonymization bookkeeping (GDPR account-deletion seam).
// Durability register (JEBV4-154, AUDIT-A IN-MEM-LIVE) — the gateway's own
// per-owner retained-row anonymization counters used to live ONLY in process
// memory, so the record of which financial rows had already been pseudonymized
// for a deleted user was LOST on every restart/replica move (money + GDPR — the
// highest-risk remaining in-memory store). PostgresFinancialLedger
// (financial_ledger_anonymization table, migration 0030) is the durable system
// of record whenever GatewayPostgres:ConnectionString is configured; the
// in-memory store stays the dev/CI/test fallback when it is absent — the
// established FAIL-OPEN-then-gate pattern (StoreDurabilityGuard now enforces the
// Postgres store in prod-like envs).
builder.Services.AddSingleton<InMemoryFinancialLedger>();
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IFinancialLedgerAnonymizer, JeebGateway.Users.PostgresFinancialLedger>();
}
else
{
    builder.Services.AddSingleton<IFinancialLedgerAnonymizer>(sp => sp.GetRequiredService<InMemoryFinancialLedger>());
}
builder.Services.AddSingleton<InMemoryAccountDeletionStore>();
// Durability register #15 — account-deletion (GDPR 30-day purge SLA). Postgres-backed
// (account_deletions, migration 0010) + the AccountDeletionPurgeWorker background sweeper
// when GatewayPostgres is configured, so a queued deletion and its purge deadline survive a
// restart. In-memory fallback (no worker) for dev/CI/test — today's behaviour.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IAccountDeletionStore,
        JeebGateway.Users.PostgresAccountDeletionStore>();
    builder.Services.AddSingleton<JeebGateway.Users.AccountDeletionPurgeWorker>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<JeebGateway.Users.AccountDeletionPurgeWorker>());
}
else
{
    builder.Services.AddSingleton<IAccountDeletionStore>(sp => sp.GetRequiredService<InMemoryAccountDeletionStore>());
}

// Data-export pipeline (T-backend-042, GDPR-like right of access).
// POST /users/me/data-export queues a full export (profile, orders,
// ratings, chat history); a background processor packages the bytes,
// stamps a single-use download token, and notifies the user. The 72-hour
// SLA lives in DataExportOptions.Sla. Production wiring will swap the
// in-memory store/providers for the Postgres-backed worker and an NSwag
// notification-service client.
builder.Services.Configure<DataExportOptions>(builder.Configuration.GetSection(DataExportOptions.SectionName));
// Durability register #16 — data-export (GDPR 72-hr SLA + single-use download tokens).
// Postgres-backed (data_exports, migration 0023) + the DataExportWorker SLA sweeper when
// GatewayPostgres is configured, so a queued export, its download token, and its SLA
// deadline survive a restart. The existing DataExportProcessor (packaging) resolves
// IDataExportStore and drives the durable store transparently; the new worker only marks
// overdue rows failed (complementary, not a duplicate). In-memory fallback for dev/CI/test.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Users.DataExport.PostgresDataExportStore>();
    builder.Services.AddSingleton<IDataExportStore>(sp =>
        sp.GetRequiredService<JeebGateway.Users.DataExport.PostgresDataExportStore>());
    builder.Services.AddSingleton<JeebGateway.Users.DataExport.DataExportWorker>();
    builder.Services.AddHostedService(sp =>
        sp.GetRequiredService<JeebGateway.Users.DataExport.DataExportWorker>());
}
else
{
    builder.Services.AddSingleton<IDataExportStore, InMemoryDataExportStore>();
}
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
builder.Services.Configure<UmJwtOptions>(builder.Configuration.GetSection(UmJwtOptions.SectionName));
// JEB-1502: register FakeTimeProvider as the singleton TimeProvider so the test
// control-plane can shift the clock for ALL time-dependent background jobs without
// any per-job patching. At zero offset this is behaviourally identical to
// TimeProvider.System — no observable difference in production.
// AddSingleton (not TryAdd) so our registration wins over any earlier internal
// TryAdd from AddRateLimiter/AddAuthorization.
builder.Services.AddSingleton<JeebGateway.TestControlPlane.FakeTimeProvider>(
    _ => new JeebGateway.TestControlPlane.FakeTimeProvider(TimeProvider.System));
builder.Services.AddSingleton<TimeProvider>(
    sp => sp.GetRequiredService<JeebGateway.TestControlPlane.FakeTimeProvider>());
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

// ---------------------------------------------------------------------------
// S02 dual-role identity seam (ADR-004 upgrade-not-switch) — the user-management
// adapter the gateway thin-BFF orchestrates for phone find-or-create (F-C) and the
// GET /v1/users/me read (F-B). The ADR-003 token-reissuing role-switch member on
// this client is now DORMANT (no caller): a client is upgraded to jeeber by real
// S03 KYC approval and the next session mint carries the full role set — there is
// no switch call. Hand-authored adapter over the SAME UserManagementServiceApi base
// address, replaced by a regenerated NSwag client once the UM keystone deploys.
// Org-standard Polly v8 resilience pipeline (N9: retry w/ jitter + circuit breaker
// + per-attempt timeout). 30s profile cache-aside backs GET /v1/users/me (F-B).
// ---------------------------------------------------------------------------
builder.Services.AddMemoryCache();
builder.Services
    .AddHttpClient<JeebGateway.Users.IUserManagementDualRoleClient,
                   JeebGateway.Users.HttpUserManagementDualRoleClient>(client =>
    {
        var apiUrl = builder.Configuration["UserManagementServiceApi:BaseUrl"];
        if (!string.IsNullOrEmpty(apiUrl))
        {
            client.BaseAddress = new Uri(apiUrl);
        }
        client.Timeout = TimeSpan.FromSeconds(30);
    })
    .AddHttpMessageHandler<JeebGateway.Services.Bff.BearerForwardingHandler>()
    .AddStandardResilienceHandler();

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
// IGeoIndex is INTENTIONALLY in-memory (JEBV4-156) — it is a DERIVED, rebuildable
// hot-path spatial index, NOT a store of record, so it must NOT be migrated to
// Postgres. The Jeeber online-presence system of record is the durable Postgres
// `jeeber_availability` table (is_online / vehicle_type / last_location / last_seen_at),
// owned by IAvailabilityStore → PostgresAvailabilityStore (already a Critical durable
// store). This geo index is only the spatial ACCELERATION layer over that truth; its
// production target is a Redis GEO sorted set (jeeber:online:geo, GEOADD/GEOSEARCH —
// see db/JEEBER_LOCATION_DESIGN.md), an explicit hot-path cache. PostgresAvailabilityStore
// writes the durable row and then updates this index, so it is fully rebuildable from
// Postgres and its loss on restart costs only a warm-up, never authoritative data.
// Tracked as IntentionalInMemory (not the migration backlog) in StoreDurabilityGuard.
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
            sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>(),
            sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            // fix/offer-visibility (run-23 CHECK C): the submit-time routing index + the
            // request read-model let ListForJeeberAsync recover the jeeber's own (incl.
            // TERMINAL) offers through the owner-scoped request-list route — offer-service
            // exposes no jeeber-scoped list route.
            sp.GetRequiredService<IOfferRequestIndex>(),
            sp.GetRequiredService<JeebGateway.Requests.IRequestsStore>());
    }

    return sp.GetRequiredService<InMemoryPendingOffersStore>();
});
// Offer → request routing index (S07 accept saga). Records the immutable
// offerId → requestId pairing at submit time so the offer-scoped accept route
// (POST /offers/{id}/accept) can forward to the request-scoped offer-service
// accept saga under FeatureFlags:UseUpstream:Offer. Routing concern only — no
// auction domain state lives here.
// The in-memory index is the fast, authoritative-within-instance read/write model.
// Registered as its concrete type so the durable decorator (wired in the
// jeeb-state-service block below, only when state-service is enabled) can compose it
// as its local cache + fallback. The IOfferRequestIndex mapping defaults to this
// in-memory instance; when state-service is wired it is re-pointed at the durable
// write-through decorator (last registration wins). Pre-S08 behaviour is unchanged
// when state-service is off.
builder.Services.AddSingleton<InMemoryOfferRequestIndex>();
builder.Services.AddSingleton<IOfferRequestIndex>(
    sp => sp.GetRequiredService<InMemoryOfferRequestIndex>());
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
// Durability register #9 — availability (admin ops-map + auto-offline). Postgres-backed
// (jeeber_availability, migration 0003 + zone/last_interaction_at migration 0026) when
// GatewayPostgres is configured so the gateway-owned availability view survives a restart;
// matching is unaffected. In-memory fallback for dev/CI/test.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<IAvailabilityStore,
        JeebGateway.Availability.PostgresAvailabilityStore>();
}
else
{
    builder.Services.AddSingleton<IAvailabilityStore, InMemoryAvailabilityStore>();
}
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
// Gap 1 flag-gated store swap (BanService precedent): when
// FeatureFlags:UseUpstream:Geolocation is ON, the record-of-truth is the shared
// geolocation-service via GeoServiceLocationStore (NSwag client); default OFF keeps
// the in-memory store so neither the controller nor the SSE loop branch on the flag.
if (builder.Configuration.GetValue<bool>("FeatureFlags:UseUpstream:Geolocation"))
{
    builder.Services.AddSingleton<ILocationStore, JeebGateway.Tracking.GeoServiceLocationStore>();
}
else
{
    builder.Services.AddSingleton<ILocationStore, InMemoryLocationStore>();
}
// S09 (JEB-54): shared delivery-participant resolver backing the live-tracking
// SSE alias, the delivery-scoped location ingest authz, and the settlement-intent
// read. Stateless BFF composition over IRequestsStore + IDeliveryServiceClient —
// honours the canonical-vs-mirror split (FeatureFlags:UseUpstream:Delivery).
builder.Services.AddSingleton<IDeliveryParticipantResolver, DeliveryParticipantResolver>();

// Real-time chat (T-backend-012) — REMOVED.
// The jeeb-specific SignalR hub (/hubs/chat), ChatDispatcher, and in-memory
// presence tracker have been removed in favour of the salehly sibling mirror:
// ChatController is now a stateless passthrough REST shim over the generic
// chat-service via the NSwag ServiceChatClient (registered above). Real-time
// fan-out is a chat-service / realtime-communication-service concern, not a
// gateway one.

// Wave 2-3 backend services.
// T-backend-017 / JEB-57: Weekly settlement batch processing.
// InMemorySettlementBatchStore DELETED (G2 gate). Replaced by PostgresSettlementBatchStore
// (when GatewayPostgres:ConnectionString is set) or InMemoryFallbackSettlementBatchStore (dev/CI).
builder.Services.Configure<JeebGateway.Financials.WeeklySettlementOptions>(
    builder.Configuration.GetSection(JeebGateway.Financials.WeeklySettlementOptions.SectionName));
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<JeebGateway.Financials.ISettlementBatchStore,
        JeebGateway.Financials.PostgresSettlementBatchStore>();
}
else
{
    builder.Services.AddSingleton<JeebGateway.Financials.ISettlementBatchStore>(sp =>
        new JeebGateway.Financials.InMemoryFallbackSettlementBatchStore(
            sp.GetRequiredService<JeebGateway.Financials.ISettlementStore>()));
}
// Register WeeklySettlementBatch as a singleton so the WS-D job registry can resolve it
// by concrete type. AddHostedService uses the same singleton instance.
builder.Services.AddSingleton<JeebGateway.Financials.WeeklySettlementBatch>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<JeebGateway.Financials.WeeklySettlementBatch>());

// T-backend-018 / JEB-1434 / JEB-1465: Earnings aggregation API.
// When FeatureFlags:UseUpstream:Earnings=true the scoped
// WalletEarningsAggregationService reads live gross revenue from the shared
// wallet-service (Transaction/holder/{holderId}/credit-revenue) instead of
// summing the in-memory settlement rows (which are always zero on a cold start).
// Default-OFF: flip to true in Production once wallet-service is confirmed healthy.
// JEBV4-283: ALWAYS register the gateway's OWN settlement aggregation as a concrete service so
// the jeeber COD-earnings READ (the app's /v1/jeeb/earnings via JeebEarningsBffController) reads
// gateway-owned settlement rows DIRECTLY, independent of the UseUpstream:Earnings flag below. That
// flag only selects which IEarningsAggregationService the legacy/admin earnings surfaces bind; when
// it is ON it routes the interface to WalletEarningsAggregationService (wallet gross credit-revenue),
// which does NOT include COD settlement commission — so the app must not depend on the interface for
// recorded COD earnings.
builder.Services.AddSingleton<JeebGateway.Financials.EarningsAggregationService>();

if (banFlags.Earnings)
{
    builder.Services.AddScoped<JeebGateway.Financials.IEarningsAggregationService,
        JeebGateway.Financials.WalletEarningsAggregationService>();
}
else
{
    // Bind the interface to the SAME concrete singleton registered above (one instance).
    builder.Services.AddSingleton<JeebGateway.Financials.IEarningsAggregationService>(
        sp => sp.GetRequiredService<JeebGateway.Financials.EarningsAggregationService>());
}

// T-backend-019 / S10 H6 (JEB-59): Earnings PDF statement generation.
// Real application/pdf via QuestPDF (Community license set below), bilingual
// JEB-59: EarningsStatement config (signed URL TTL + HMAC key)
builder.Services.Configure<JeebGateway.Financials.EarningsStatementOptions>(
    builder.Configuration.GetSection(JeebGateway.Financials.EarningsStatementOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Financials.EarningsStatementTokenService>();
// en/ar — replaces the legacy text/plain stub.
QuestPDF.Settings.License = QuestPDF.Infrastructure.LicenseType.Community;

// JEB-59: register NotoSansArabic font for correct Arabic glyph shaping in Docker/CI.
// QuestPDF.Drawing.FontManager is the correct namespace (QuestPDF.Infrastructure.FontManager
// was renamed in 2022.8+).
{
    var fontPath = System.IO.Path.Combine(
        System.AppContext.BaseDirectory, "assets", "fonts", "NotoSansArabic-Regular.ttf");
    if (System.IO.File.Exists(fontPath))
    {
        using var stream = System.IO.File.OpenRead(fontPath);
        QuestPDF.Drawing.FontManager.RegisterFont(stream);
    }
}

// JEB-59: cached PDF generator (inner = QuestPdf, outer = IMemoryCache decorator)
builder.Services.AddSingleton<JeebGateway.Financials.QuestPdfEarningsStatementGenerator>();
builder.Services.AddSingleton<JeebGateway.Financials.IEarningsPdfGenerator>(sp =>
    new JeebGateway.Financials.CachedEarningsPdfGenerator(
        sp.GetRequiredService<JeebGateway.Financials.QuestPdfEarningsStatementGenerator>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
        sp.GetRequiredService<TimeProvider>()));

// T-backend-033: Admin finance dashboard API.
builder.Services.AddSingleton<JeebGateway.Financials.IAdminFinanceDashboardService, JeebGateway.Financials.AdminFinanceDashboardService>();

// T-backend-021: 7-day rating reveal cron job.
// JEB-1502: registered as singleton first so ITestJobRegistry can resolve it and call
// SweepOnceAsync (the same code path the background loop uses).
builder.Services.Configure<JeebGateway.Ratings.RatingRevealOptions>(
    builder.Configuration.GetSection(JeebGateway.Ratings.RatingRevealOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Ratings.RatingRevealJob>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<JeebGateway.Ratings.RatingRevealJob>());

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
// Honor the owner's flat lever name WHISPER_FAKE_TRANSCRIBE in addition to the
// section-based key Whisper:FakeTranscribe. .NET's default env provider only maps
// double-underscore keys (Whisper__FakeTranscribe), so we explicitly fold the flat
// name in here when present. Section/Whisper__ keys still win if both are set.
var whisperFakeFlat = Environment.GetEnvironmentVariable("WHISPER_FAKE_TRANSCRIBE");
if (!string.IsNullOrWhiteSpace(whisperFakeFlat)
    && string.IsNullOrWhiteSpace(builder.Configuration["Whisper:FakeTranscribe"]))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Whisper:FakeTranscribe"] = whisperFakeFlat
    });
}

builder.Services.Configure<WhisperOptions>(builder.Configuration.GetSection(WhisperOptions.SectionName));

// STT seam (Track C): select the REAL OpenAI Whisper client when STT is enabled for
// real (FakeTranscribe=false) AND an API key is present; otherwise fall back to the
// network-free FakeWhisperClient. The real WhisperClient is never deleted — it remains
// the production path and is the only branch that opens an HttpClient to OpenAI.
var whisperOpts = builder.Configuration.GetSection(WhisperOptions.SectionName).Get<WhisperOptions>()
                  ?? new WhisperOptions();
var useRealWhisper = !whisperOpts.FakeTranscribe && !string.IsNullOrWhiteSpace(whisperOpts.ApiKey);
if (useRealWhisper)
{
    builder.Services.AddHttpClient<IWhisperClient, WhisperClient>((sp, http) =>
    {
        var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<WhisperOptions>>().Value;
        http.BaseAddress = new Uri(opts.BaseUrl.TrimEnd('/') + "/");
        http.Timeout = Timeout.InfiniteTimeSpan;
    });
}
else
{
    builder.Services.AddSingleton<IWhisperClient, FakeWhisperClient>();
}
builder.Services.AddSingleton<IWhisperCircuitBreaker, WhisperCircuitBreaker>();
// IAudioStore holds the raw voice-note BYTES (WhisperAudio.Content). Large audio
// blobs deliberately do NOT go into the gateway Postgres DB — their durable home is
// the voice-transcription-service's S3-compatible storage (see IAudioStore's own
// doc-comment), which the gateway must not reach into (org no-coupling law). In the
// gateway it is only a TRANSIENT in-process buffer holding the bytes already in-hand
// at the moment of fallback (SaveAsync is the ONLY method ever called — there is no
// GetAsync / drain-back path in the gateway today), NOT a store of record. It is left
// in-memory ON PURPOSE and is documented as an intentional transient on the AUDIT-A
// backlog (StoreDurabilityGuard.KnownInMemoryBacklog) — not a pending migration.
builder.Services.AddSingleton<IAudioStore, InMemoryAudioStore>();
// Durability follow-up — transcription fallback queue (JEBV4-126). This queue holds
// only SMALL metadata rows (audio_id, reason, queued_at) for voice notes whose
// transcription fell back and must be re-driven once Whisper recovers; in-memory it
// evaporated on every restart, silently resetting the pending backlog and the
// PendingQueueDepth on the Whisper health check + status endpoint. Postgres-backed
// (transcription_fallback_queue, migration 0033) whenever GatewayPostgres:ConnectionString
// is configured — the established FAIL-OPEN-then-gate pattern (StoreDurabilityGuard now
// enforces the Postgres impl in prod-like envs). The in-memory queue stays the
// dev/CI/test fallback when the connection string is absent.
if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
{
    builder.Services.AddSingleton<ITranscriptionFallbackQueue, PostgresTranscriptionFallbackQueue>();
}
else
{
    builder.Services.AddSingleton<ITranscriptionFallbackQueue, InMemoryTranscriptionFallbackQueue>();
}
builder.Services.AddSingleton<IFallbackTranscriptionProvider, NoOpFallbackTranscriptionProvider>();
builder.Services.AddScoped<ITranscriptionService, ResilientTranscriptionService>();
builder.Services.AddHealthChecks()
    .AddCheck<WhisperHealthCheck>("whisper", tags: new[] { "ready" });

// AUDIT-A (FIX-1) readiness surface for the fail-closed durability gate. "ready"-tagged so
// /health/ready reports 503 if any critical store of record is in-memory in a prod-like env
// (belt-and-suspenders on top of the boot gate wired after builder.Build()). No-op-Healthy in
// Development/Testing. See JeebGateway.Infrastructure.StoreDurabilityGuard.
builder.Services.AddHealthChecks()
    .AddCheck<JeebGateway.Infrastructure.StoreDurabilityHealthCheck>("store-durability", tags: new[] { "ready" });

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

    // JM-063 — Jeeb support tickets, backed by jeeb-state-service per ADR-0005 (the opaque
    // KV above). The gateway stays stateless: a ticket is an opaque support-ticket:{id} row.
    builder.Services.AddSingleton<JeebGateway.JeebSupport.IJeebSupportTicketStore,
        JeebGateway.JeebSupport.StateServiceSupportTicketStore>();

    // S08 (A3/N9) — DURABLE offer→request routing. Re-point IOfferRequestIndex at the
    // write-through decorator so the offerId → (requestId, jeeberId) pairing survives a
    // gateway bounce and is shared across replicas (mirrored into the R1 idempotency KV,
    // GET-by-key bounce-survivable). The InMemoryOfferRequestIndex registered above is
    // composed as the decorator's fast local cache + degrade-don't-fail fallback. This
    // overrides the default in-memory IOfferRequestIndex mapping (last registration wins)
    // and fixes the per-replica / lost-on-restart spurious 404 on offer edit/accept.
    builder.Services.AddSingleton<IOfferRequestIndex,
        JeebGateway.StateService.Durable.StateServiceOfferRequestIndex>();

    // R8 — rate-limit + handover locks (keyed by bucket/lockKey ⇒ bounce-survivable).
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateRateLimitStore,
        JeebGateway.StateService.RateLimiting.StateServiceRateLimitStore>();
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateLockStore,
        JeebGateway.StateService.RateLimiting.StateServiceLockStore>();

    // R6 — strikes + cancellation counters; R7 — OTP-escalation (durable writes).
    builder.Services.AddSingleton<JeebGateway.StateService.Strikes.IStateStrikeWriter,
        JeebGateway.StateService.Strikes.StateServiceStrikeWriter>();

    // JEB-1508: durable TTL-sweep expiry recorder. Re-points IRequestExpiryRecorder at the
    // state-service-backed implementation using the R1 idempotency KV. Overrides the no-op
    // singleton registered above; last-wins DI semantics. A state-service blip degrades to
    // in-memory-only (the no-op contract) rather than failing the sweep.
    builder.Services.AddSingleton<IRequestExpiryRecorder,
        JeebGateway.Requests.StateServiceRequestExpiryRecorder>();

    // R2/R3/R4/R5 — durable write-through (writes land; see contract gap note).
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateRefreshFamilyWriter,
        JeebGateway.StateService.Durable.StateServiceRefreshFamilyWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateKycWriter,
        JeebGateway.StateService.Durable.StateServiceKycWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateRatingWriter,
        JeebGateway.StateService.Durable.StateServiceRatingWriter>();
    builder.Services.AddSingleton<JeebGateway.StateService.Durable.IStateDisputeWriter,
        JeebGateway.StateService.Durable.StateServiceDisputeWriter>();

    // ADR-0001 remediation (run #1 follow-up): the dispute STORE itself is now durable.
    // Re-points IDisputeStore from the in-memory MVP store (rows lost on every gateway
    // bounce / replica move — the flagged ADR-0001 violation) to the state-service-backed
    // store, which persists the full Jeeb dispute row as an opaque KV body + owner/delivery
    // prefix indexes (the PR #206 support-ticket pattern). Overrides the InMemoryDisputeStore
    // registered earlier (last-wins DI); a state-service blip is absorbed by the typed
    // client's circuit-breaker rather than failing the file/list path.
    builder.Services.AddSingleton<IDisputeStore, StateServiceDisputeStore>();

    // Durability register #3 — refresh-token store. Re-points IRefreshTokenStore from the
    // in-memory MVP store (rows lost on every gateway bounce → refresh-reuse detection and
    // active-token revocation evaporate) to the state-service-backed store, which persists
    // the token row + status chain + hash/user index in the R1 idempotency KV (registered
    // above). Overrides the InMemoryRefreshTokenStore registered earlier (last-wins DI).
    builder.Services.AddSingleton<IRefreshTokenStore,
        JeebGateway.Tokens.StateServiceRefreshTokenStore>();

    // Durability register #4 — dispute-case (v2) store. Re-points IDisputeCaseStore from the
    // in-memory MVP store to the state-service-backed store (opaque KV body + delivery/user
    // prefix indexes), mirroring the sibling StateServiceDisputeStore above so escalated
    // dispute cases survive a bounce. Overrides InMemoryDisputeCaseStore (last-wins DI).
    builder.Services.AddSingleton<IDisputeCaseStore,
        JeebGateway.Disputes.V2.StateServiceDisputeCaseStore>();

    // Add jeeb-state-service to the aggregate-health roster (now 18 checks).
    builder.Services.AddHealthChecks()
        .AddUrlGroup(
            new Uri(stateOptions.BaseUrl.TrimEnd('/') + "/health"),
            name: "jeeb-state-service",
            failureStatus: Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded,
            tags: new[] { "ready", "downstream" });
}
else
{
    // local/CI fallback (no live jeeb-state-service): the support store still needs an
    // opaque KV to resolve, so back it with the in-process idempotency KV — the SAME
    // degrade-to-in-memory contract the other durable stores use when unwired. Production
    // (state-service wired) NEVER takes this path; the support row lives in jeeb-state-service.
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IIdempotencyStore,
        JeebGateway.StateService.Idempotency.InMemoryIdempotencyStore>();
    builder.Services.AddSingleton<JeebGateway.JeebSupport.IJeebSupportTicketStore,
        JeebGateway.JeebSupport.StateServiceSupportTicketStore>();
}

// Global RFC 7807 ProblemDetails + last-line exception handler. Guarantees an
// unhandled exception (notably an upstream non-2xx that bubbles up as an
// HttpRequestException) is mapped to application/problem+json instead of an
// opaque raw 500 — the S07 root-cause hardening for "negatives masked to 500".
// Additive: controllers that already return typed results never throw, so they
// are untouched.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<JeebGateway.Infrastructure.UpstreamExceptionHandler>();

// ---------------------------------------------------------------------------
// Middleware pipeline
// ---------------------------------------------------------------------------

var app = builder.Build();

if (app.Configuration.GetValue<bool>("FeatureFlags:UseUpstream:Ratings"))
{
    app.Logger.LogCritical(
        "FeatureFlags:UseUpstream:Ratings is ON, but feedback-service does not expose list-expired-windows or mark-revealed/closed rating APIs; the gateway reveal sweep is registered fail-closed and will not fabricate upstream reveal state.");
}

// AUDIT-A (FIX-1) — fail-closed durability gate. Refuses to start a prod-like gateway whose
// money/identity/audit/legal/security stores silently fell back to in-memory because a durability
// selector env var was dropped/typo'd (the "green health, corrupt state" class this program closes).
// No-op in Development/Testing. Runs before app.Run(), so a mis-provisioned prod deploy crashes on
// boot with a message naming each offending store instead of serving ephemeral state. Mirrors
// JwtSigningKeyGuard. Rollback = delete this call (pure additive; changes no store registration).
JeebGateway.Infrastructure.StoreDurabilityGuard.EnsureDurable(
    app.Services, app.Environment,
    app.Services.GetRequiredService<ILogger<Program>>());

// JEB-1502: populate the test job registry. Each entry delegates to the job's
// own sweep method — the SAME code path the background scheduler calls. No
// test-only forks. settlement-batch is registered here as a placeholder;
// WS-A will wire in the real RunBatchAsync after implementing durable settlement.
var testJobRegistry = app.Services.GetRequiredService<JeebGateway.TestControlPlane.ITestJobRegistry>();
var ratingRevealJob = app.Services.GetRequiredService<JeebGateway.Ratings.RatingRevealJob>();
var requestExpirySweeper = app.Services.GetRequiredService<RequestExpirySweeper>();
var weeklyBatch = app.Services.GetRequiredService<JeebGateway.Financials.WeeklySettlementBatch>();

testJobRegistry.Register(new JeebGateway.TestControlPlane.RegisteredJob
{
    Name = "rating-reveal",
    Description = "Reveal mutually rated windows and close one-sided windows past the 7-day blind window (RatingRevealJob.SweepOnceAsync).",
    RunAsync = ct => ratingRevealJob.SweepOnceAsync(ct)
});
testJobRegistry.Register(new JeebGateway.TestControlPlane.RegisteredJob
{
    Name = "request-expiry-sweep",
    Description = "Expire overdue requests and send nudges (RequestExpirySweeper.SweepOnceAsync).",
    RunAsync = ct => requestExpirySweeper.SweepOnceAsync(ct)
});
// settlement-batch: placeholder; WS-A registers the real delegate during Wave 2.
testJobRegistry.Register(new JeebGateway.TestControlPlane.RegisteredJob
{
    Name = "settlement-batch",
    Description = "Weekly settlement batch (WeeklySettlementBatch.RunBatchAsync). Placeholder — WS-A wires durable impl.",
    RunAsync = ct => weeklyBatch.RunBatchAsync(ct)
});

// Must be registered early in the pipeline so it wraps the whole request.
app.UseExceptionHandler();
app.UseStatusCodePages(async statusCodeContext =>
{
    var httpContext = statusCodeContext.HttpContext;
    var problemDetails = httpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
    await problemDetails.TryWriteAsync(new ProblemDetailsContext
    {
        HttpContext = httpContext,
        ProblemDetails = new ProblemDetails
        {
            Status = httpContext.Response.StatusCode
        }
    });
});

// STT seam visibility (Track C): make the active Whisper path obvious in startup logs.
if (useRealWhisper)
{
    app.Logger.LogInformation(
        "Whisper STT: REAL OpenAI client active (model={Model}, lang={Language}).",
        whisperOpts.Model, whisperOpts.Language);
}
else if (whisperOpts.FakeTranscribe)
{
    app.Logger.LogInformation(
        "Whisper STT: FAKE client active (Whisper:FakeTranscribe=true). No external calls.");
}
else
{
    app.Logger.LogWarning(
        "Whisper STT: FAKE client active because no Whisper:ApiKey is configured "
        + "while FakeTranscribe=false. Set Whisper__ApiKey to enable REAL transcription.");
}

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

// JEB-67 / T-BE-031 AC7 / C15 — Swagger UI exposure. Two mutually-exclusive
// branches:
//   (a) Development OR Testing  => Swagger OPEN, no auth gate. Local/CI only;
//       these environments are never the public Production host.
//   (b) Features:Swagger:Enabled == true (additive flag, runs under ANY other
//       environment INCLUDING Production) => Swagger mounted behind an
//       admin-ROLE gate: any /swagger request without an authenticated principal
//       in the "admin" role gets 404 (admin => 200, non-admin => 404).
// Otherwise (the default for Production: flag false) => Swagger never mounted,
// so /swagger* returns 404.
//
// The admin gate was previously keyed on EnvironmentName == "Staging", which
// never executes on the live Production host. It is re-keyed here onto the
// Features:Swagger:Enabled flag (committed-false everywhere; flipped on only via
// the deploy-to-jeeb.yml `swagger_ui` input) so the SAME admin gate runs under
// Production. jeeb.fds-1.com is PUBLIC, so we deliberately do NOT reuse the open
// Development/Testing branch when enabling Swagger in Production — that would
// leak the full route surface unauthenticated. ASPNETCORE_ENVIRONMENT is never
// flipped to enable this (that would also open the /dev surface + regress other
// prod hardening).
var swaggerEnabled = builder.Configuration
    .GetSection(JeebGateway.Security.SwaggerOptions.SectionName)
    .Get<JeebGateway.Security.SwaggerOptions>()?.Enabled ?? false;

if (app.Environment.IsDevelopment()
    || string.Equals(app.Environment.EnvironmentName, "Testing", StringComparison.OrdinalIgnoreCase))
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Jeeb Gateway v1"));
}
else if (swaggerEnabled)
{
    app.UseWhen(
        ctx => ctx.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase),
        branch =>
        {
            branch.Use(async (ctx, next) =>
            {
                // This branch is registered BEFORE app.UseAuthentication() in the
                // pipeline, so ctx.User is not yet populated by the JWT bearer
                // handler here. Authenticate the bearer scheme explicitly so a
                // live JWT (e.g. SETUP-2's roles:[admin] token) populates the
                // principal, then resolve the role through the gateway's shared
                // UserIdentity — which honors BOTH the JWT "roles" claim AND the
                // edge-injected X-User-Roles header (the gateway's dual MVP
                // identity model). admin => 200, everyone else (incl. anon) => 404.
                var auth = await ctx.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                if (auth.Succeeded && auth.Principal is not null)
                {
                    ctx.User = auth.Principal;
                }
                if (!JeebGateway.Users.UserIdentity.IsAdmin(ctx))
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

// F2 (Leg-12): fail-closed 404 for [DevOnly] endpoints BEFORE authentication, so an
// anonymous request to a disabled dev/diagnostic route gets 404 (route does not exist)
// rather than the 401 the authorization middleware would otherwise return (which leaks
// route existence). Placed after UseRouting (endpoint metadata resolved) and before
// UseAuthentication/UseAuthorization. Pure pass-through when Features:DevEndpoints:Enabled.
app.UseMiddleware<JeebGateway.Security.DevOnlyEndpointGuardMiddleware>();

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
app.MapPrometheusScrapingEndpoint("/metrics").AllowAnonymous();

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
}).AllowAnonymous();
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = AggregateHealthResponseWriter.WriteAsync,
}).AllowAnonymous();
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false, // liveness alias — never gate on downstreams
}).AllowAnonymous();
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
}).AllowAnonymous();

// JEB-57: TODO — register WeeklySettlementBatch in WS-D test-control-plane job registry
// (JEB-1502, fix/JEB-1502).  When that branch is merged, add:
//
//   var registry = app.Services.GetService<JeebGateway.TestControlPlane.ITestJobRegistry>();
//   if (registry is not null)
//   {
//       var batch = app.Services.GetRequiredService<JeebGateway.Financials.WeeklySettlementBatch>();
//       registry.Register(new JeebGateway.TestControlPlane.RegisteredJob
//       {
//           Name        = "settlement-batch",
//           Description = "Weekly COD settlement batch (durable Postgres, JEB-57 Wave-2 impl).",
//           RunAsync    = ct => batch.RunBatchAsync(ct),
//       });
//   }

app.Run();

// Required for WebApplicationFactory<Program> integration tests.
public partial class Program { }
