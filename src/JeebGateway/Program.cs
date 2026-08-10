using System.Text;
using JeebGateway.Admin;
using JeebGateway.Artifacts;
using JeebGateway.Availability;
using JeebGateway.Cases;
using JeebGateway.Disputes;
using JeebGateway.Disputes.V2;
using JeebGateway.Extensions;
using JeebGateway.Financials;
using JeebGateway.Kyc;
using JeebGateway.Jobs;
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
var isTestHarness = builder.Environment.IsEnvironment("Testing")
                    || builder.Environment.IsDevelopment();

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

// AdminEvidence:TokenKey is a dedicated secret boundary, enforced only once the
// admin portal is on: the live BFF keeps booting with zero new config.
if (builder.Configuration.GetValue<bool>("AdminOidc:Enabled"))
{
    JeebGateway.Tokens.JwtSigningKeyGuard.EnsureNotPlaceholder(
        builder.Configuration["AdminEvidence:TokenKey"], builder.Environment, "AdminEvidence:TokenKey");
}

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
builder.Services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler,
    JeebGateway.Auth.Oidc.ExternalAdminSessionAuthorizationHandler>();
builder.Services.Configure<JeebGateway.Auth.Oidc.AdminOidcOptions>(
    builder.Configuration.GetSection(JeebGateway.Auth.Oidc.AdminOidcOptions.SectionName));
builder.Services.AddHttpClient("AdminOidc", client =>
    client.Timeout = TimeSpan.FromSeconds(10))
    .ConfigurePrimaryHttpMessageHandler(
        JeebGateway.Auth.Oidc.AdminOidcHttpTransport.CreateHandler);

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
            policy =>
            {
                policy.AddAuthenticationSchemes(GatewayBearerScheme)
                // Layer 1 identity check — accepts a validated GatewayBearer principal OR the trusted
                // edge X-User-Id header, IDENTICALLY to the ADR-004 FallbackPolicy. Using
                // GatewayAudienceRequirement (not bare RequireAuthenticatedUser()) is what preserves
                // the admin/edge X-User-Id + X-User-Roles path (ADR-005 §7, test T5): a header-only
                // edge caller has no authenticated principal, so RequireAuthenticatedUser() would 401
                // it and break the path the ADR mandates keeping. Layer 1 here -> 401 on failure.
                .AddRequirements(new JeebGateway.Auth.GatewayAudienceRequirement())
                // Layer 2 user-type capability check -> 403 on failure (CapabilityForbiddenResultHandler).
                .AddRequirements(new JeebGateway.Auth.Capabilities.CapabilityRequirement(capability));

                // Back-office essentials have a third, issuer-bound layer. They
                // require a gateway bearer minted from the configured external
                // OIDC ceremony; ordinary gateway/mobile tokens and the trusted
                // edge-header compatibility path never satisfy admin.*.
                if (JeebGateway.Auth.Oidc.AdminCapabilityBoundary
                    .RequiresExternalOperatorSession(capability))
                    policy.AddRequirements(
                        new JeebGateway.Auth.Oidc.ExternalAdminSessionRequirement());
            });
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
        Description = "BFF gateway aggregating downstream Jeeb services.",
        License = new Microsoft.OpenApi.Models.OpenApiLicense
        {
            Name = "Proprietary",
        },
    });
    options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Gateway-issued administrator access token."
    });
    options.OperationFilter<JeebGateway.Security.BearerSecurityOperationFilter>();
    options.OperationFilter<JeebGateway.OpenApi.GatewayOperationSummaryFilter>();
    options.DocumentFilter<JeebGateway.OpenApi.GatewayServerDocumentFilter>();
    options.SchemaFilter<JeebGateway.OpenApi.GatewayEvidenceSchemaFilter>();
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
    .AddCheck("self", () => HealthCheckResult.Healthy("process alive"), tags: new[] { "live" })
    .AddCheck<JeebGateway.Auth.Oidc.AdminOidcConfigurationHealthCheck>(
        "admin-oidc-configuration",
        failureStatus: HealthStatus.Unhealthy,
        tags: new[] { "ready" });

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
// CMS is a stateless compatibility adapter. bundler-service owns documents,
// drafts, immutable versions, and publication history; the gateway owns no CMS
// persistence or fallback store.
builder.Services.AddCmsAuthoringPlane(builder.Configuration);
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

// GW5 / W1.6-gateway — synchronous post-accept chat settlement. The gateway
// keeps no reconciliation loop; chat-service owns retryable conversation work.
builder.Services.AddScoped<JeebGateway.Conversations.IAcceptChatSettler,
                           JeebGateway.Conversations.AcceptChatSettler>();

// S08 (D / H6,N2) — the realtime membership-ticket issuer. The /v1/realtime gate
// mints a short-lived signed ticket scoped to (conversation, viewer, role) after
// the chat-service membership check, so realtime-comunication-service can authorize
// the WS join without calling chat-service (no inter-service coupling). HS256 over
// the gateway's existing Jwt:SigningKey (the same secret the realtime Guardian
// pipeline verifies the session bearer with). Singleton — the key is read once.
builder.Services.AddSingleton<JeebGateway.Conversations.Realtime.IRealtimeTicketIssuer,
                              JeebGateway.Conversations.Realtime.RealtimeTicketIssuer>();

// Continuous courier position — the gateway half.
//
// The credential issuer for realtime-comunication-service. Its Guardian pipeline
// verifies with ITS OWN secret, not the gateway's Jwt:SigningKey, so neither the
// forwarded user bearer nor the S08 membership ticket above can authenticate against
// it. The service does ship an OPEN, UNAUTHENTICATED POST /api/auth/token that mints
// topics:["*"] for anyone; nothing here is built on that. The gateway mints its own
// credentials instead, each scoped to a single topic — publish-only for the server-side
// fan-out, subscribe-only for a client. Unconfigured (the committed default) means no
// token is minted and every dependent path fails closed.
builder.Services.Configure<JeebGateway.Realtime.RealtimeGuardianOptions>(
    builder.Configuration.GetSection(JeebGateway.Realtime.RealtimeGuardianOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Realtime.IRealtimeGuardianTokenIssuer,
                              JeebGateway.Realtime.RealtimeGuardianTokenIssuer>();

// The GPS-ingest → realtime fan-out. Queue + drainer, mirroring the
// NewRequestFanoutQueue / NewRequestFanoutProcessor pair: POST /location/update only
// ever calls the non-blocking TryEnqueue, so a realtime outage cannot fail or slow the
// location write. Explicit factory because CourierPositionQueue exposes a second,
// capacity-int ctor for tests.
builder.Services.Configure<JeebGateway.Services.Clients.GeoHistoryWriteOptions>(
    builder.Configuration.GetSection(JeebGateway.Services.Clients.GeoHistoryWriteOptions.SectionName));

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

// Sole notification-producer boundary. This client submits stable commands to
// notification-service, which owns persistence, push dispatch, retries, DLQ,
// device tokens, and delivery tracking. It intentionally has no gateway store.
builder.Services.AddTransient<JeebGateway.Notifications.NotificationServiceCredentialHandler>();
ServiceClientExtensions.AttachResilienceOnly(
    builder.Services.AddHttpClient(
        JeebGateway.Notifications.NotificationOwnerClient.HttpClientName,
        client =>
        {
            var apiUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(apiUrl))
            {
                client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            }
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<JeebGateway.Notifications.NotificationServiceCredentialHandler>());
builder.Services.AddSingleton<JeebGateway.Notifications.INotificationOwnerClient,
    JeebGateway.Notifications.NotificationOwnerClient>();

// JEB-1486 cutover step (2) — keep the deprecated jeeb.* localization ALIVE.
// The de-leak relocated the Jeeb notification taxonomy into the gateway
// (JeebNotificationCatalog) and emptied notification-service's locale catalog, so
// the running shared service no longer localizes any jeeb.* topic on its own.
// JeebNotificationCatalogSeeder re-registers every gateway-owned catalog entry,
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
// Notification templates and their lifecycle are owned by notification-service.
// The gateway performs no boot-time seeding or retry loop.

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
// out. The attached pipeline gives it the sub-100s timeout + breaker
// (a genuinely down upstream still trips and fails fast) WITHOUT retrying the
// same dispatch call. Not AttachStandardPipeline either: no bearer/ServiceAuth
// chain by design (salehly mirror).
// PUSH-BREAKER — AttachPushBreakerAndTimeout, NOT the shared
// AttachBreakerAndTimeoutOnly (which ServiceWalletClient also uses and whose
// accounting must stay strict). Same window/ratio/throughput/break duration;
// the push pipeline additionally excludes a per-user "all device tokens dead"
// 500 on api/v1/sent-payload/user/{id} from breaker accounting, so one poisoned
// recipient can no longer pin the breaker open and deny pushes to everyone else.
// Rationale + accepted residual risk: ServiceClientExtensions.ConfigurePushBreakerAndTimeout.
// SINGLE-PRODUCER CUTOVER — direct send routes are fail-closed by default. Deploy
// this gateway state before enabling notification-service's durable webhook
// dispatcher. Registration/deletion and idempotency recovery remain reachable.
builder.Services.Configure<GatewayDirectPushDispatchOptions>(
    builder.Configuration.GetSection(GatewayDirectPushDispatchOptions.SectionName));
builder.Services.AddTransient<GatewayDirectPushDispatchGuardHandler>();
ServiceClientExtensions.AttachPushBreakerAndTimeout(builder.Services.AddHttpClient("ServicePushNotificationClient", client =>
{
    var apiUrl = builder.Configuration["PushNotificationServiceApi:BaseUrl"];
    if (!string.IsNullOrWhiteSpace(apiUrl))
    {
        client.BaseAddress = new Uri(apiUrl);
    }
    client.Timeout = TimeSpan.FromSeconds(30);
}).AddHttpMessageHandler<GatewayDirectPushDispatchGuardHandler>());
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
builder.Services.AddTransient<JeebGateway.Services.Clients.IPushDispatchRecoveryClient,
    JeebGateway.Services.Clients.PushDispatchRecoveryClient>();
ServiceClientExtensions.AttachBreakerAndTimeoutOnly(
    builder.Services.AddHttpClient<JeebGateway.Notifications.JeebNotificationRecordClient>(
        JeebGateway.Notifications.JeebNotificationRecordClient.HttpClientName,
        client =>
        {
            var apiUrl = builder.Configuration["ServiceNotificationClient:BaseUrl"];
            if (!string.IsNullOrWhiteSpace(apiUrl))
            {
                client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
            }
            client.Timeout = TimeSpan.FromSeconds(30);
        }));
builder.Services.AddScoped<
    JeebGateway.Notifications.INotificationRecordWriter,
    JeebGateway.Notifications.NotificationRecordWriter>();

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

// The seam that lets a push seat use a budget big enough to actually complete without
// putting that budget in front of a user-visible response. Singleton: it holds only the
// scope factory and a logger, and creates a FRESH scope per dispatch because the notifiers
// it runs are scoped and the request scope dies with the response.
builder.Services.AddSingleton<JeebGateway.Notifications.IDetachedPushDispatcher,
    JeebGateway.Notifications.DetachedPushDispatcher>();

// b02 WAVE B.1 — the delivery-status → push trigger, on STACK B. Delivery status was the
// LAST push category still composed inside the gateway and handed to the in-gateway
// IPushNotificationService, whose InMemoryPushTransport enqueues to an in-process queue and
// reports Delivered. Six mobile surfaces polled (5s/10s/60s) precisely because the push they
// were waiting for never left the process. Scoped: composes the SCOPED
// ServicePushNotificationClient (:10040), exactly like chat/offer/new-request.
// ⛔ Do NOT "fix" the old path by re-adding an in-gateway transport that dials a push
// provider directly — permanently forbidden (the gateway must never speak to a push
// provider itself; b05/GW1 W0.6 DELETED the class and its config switch). It would not
// work anyway: IDeviceTokenStore.RegisterAsync has zero production callers, so the send
// resolves NoDevices.
builder.Services.AddScoped<JeebGateway.Notifications.IDeliveryStatusPushNotifier,
    JeebGateway.Notifications.DeliveryStatusPushNotifier>();

// BUILD-NEWREQ-PUSH — the request-created → "finding jeebers" push trigger. Best-effort
// FCM fan-out when a customer creates a delivery request (the third missing push link,
// after chat and offer). Scoped: composes the SCOPED ServicePushNotificationClient (:10040).
builder.Services.AddScoped<JeebGateway.Notifications.INewRequestPushNotifier,
    JeebGateway.Notifications.NewRequestPushNotifier>();

// P1 — new-request fan-out: options + the off-hot-path dispatch rail. The notifier stays
// SCOPED (it composes the SCOPED ServicePushNotificationClient); the queue is a singleton
// buffer and the processor is a hosted service that opens a FRESH scope per job.
// DI lifetime note (do NOT "fix" these): IAvailabilityStore = Singleton,
// ServicePushNotificationClient = Scoped, INewRequestPushNotifier = Scoped.
// Singleton-into-scoped is safe; scoped-into-singleton is the captive-dependency bug the
// per-job scope in NewRequestFanoutProcessor avoids.
builder.Services
    .AddOptions<JeebGateway.Notifications.NewRequestFanoutOptions>()
    .Bind(builder.Configuration.GetSection(JeebGateway.Notifications.NewRequestFanoutOptions.SectionName))
    .Validate(
        o => o.MaxRecipients >= 1,
        "Notifications:NewRequestFanout:MaxRecipients must be >= 1 — a non-positive cap empties the per-user set and hands control to the TopicFallbackWhenEmpty topic-blast hatch.")
    .Validate(
        o => o.KnownJeeberWindow > TimeSpan.Zero,
        "Notifications:NewRequestFanout:KnownJeeberWindow must be greater than zero.")
    .ValidateOnStart();
// No gateway queue/processor: notification-service owns command durability,
// retries and DLQ. The notifier awaits its owner calls within the request scope.

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
// Owner-scoped offer-list projection: statelessly joins offer-service rows with
// the existing user-management profile and feedback-service review reads.
builder.Services.AddScoped<JeebGateway.Availability.IOfferJeeberEnricher,
    JeebGateway.Availability.OfferJeeberEnricher>();

// F5 avatar contract: the externally reachable origin used to project stored
// profile_avatar/ refs into loadable avatar URLs (Gateway__PublicBaseUrl).
builder.Services.Configure<JeebGateway.Users.GatewayPublicOptions>(
    builder.Configuration.GetSection(JeebGateway.Users.GatewayPublicOptions.SectionName));

// T-migrate-gateway-proxies (PR-A): per-service kill switches. Each
// controller migrated in this PR checks the matching flag and falls
// back to the in-memory store when false. PR-B flips defaults to true
// and removes the in-memory stores.
builder.Services.Configure<UpstreamFeatureFlags>(
    builder.Configuration.GetSection(UpstreamFeatureFlags.SectionName));

// Firebase chat custom-token mint (POST /v1/chat/firebase-token) — the identity hop
// that lets the client read its own thread straight from Firestore instead of
// re-fetching it over REST. The signing key is referenced by absolute HOST path in
// configuration and is never committed; when unconfigured the route reports 503 and
// nothing else changes. See FirebaseCustomTokenMinter for the credential-locality
// guards and the project pinning.
builder.Services.Configure<JeebGateway.Chat.Firebase.FirebaseCustomTokenOptions>(
    builder.Configuration.GetSection(
        JeebGateway.Chat.Firebase.FirebaseCustomTokenOptions.SectionName));
builder.Services.AddSingleton<
    JeebGateway.Chat.Firebase.IFirebaseCustomTokenMinter,
    JeebGateway.Chat.Firebase.FirebaseCustomTokenMinter>();

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

// F-E (S02, JEB-37 / JEB-1422) — gateway phone admission policy + OTP-request
// burst guard, both evaluated in AuthOtpController BEFORE the one-time-password
// upstream is dialed (no upstream change). Region gate (LB-only -> invalid_country),
// E.164 parse (-> invalid_phone), and a per-IP AND per-phone sliding window
// (-> 429 rate_limited, SendOTP NOT called when throttled). Caps/region are
// configuration (Auth:Otp:Phone / Auth:Otp:RateLimit) so an env tunes them without
// a code change. Production uses the Redis-backed limiter; local test hosts use
// an explicit process-local fixture.
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
            .AddSource(CaseTelemetry.ActivitySourceName)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // T-backend-050 — Jeeb-owned per-endpoint latency meter.
            .AddMeter(RequestLatencyMetrics.MeterName)
            .AddMeter(CaseTelemetry.MeterName)
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

// JEBV4-49 (M5): the JeebPricingOptions "config-overridable rates" affordance was
// DEAD — SettlementService calls the static CommissionCalculator directly, so a
// JeebPricing appsettings override never reached settlement math (quotes and
// settlement could silently diverge). Removed the options class and this dead
// registration; CommissionCalculator's flat-10% constants are the single source
// of truth (JEBV4-43's CommissionAgreementTests guards that policy).

// Settlement money is owned by wallet-service. The legacy row-shaped
// ISettlementStore has no complete owner contract, so it fails closed rather
// than creating a gateway ledger. The enqueue marker is remote state-service
// idempotency, and every actual money post goes to wallet-service.
builder.Services.AddSingleton<ISettlementStore, UnavailableSettlementStore>();
builder.Services.AddSingleton<ISettlementEnqueueStore, StateServiceSettlementEnqueueStore>();
builder.Services.AddSingleton<WalletSettlementLedgerClient>();
builder.Services.AddSingleton<ISettlementLedgerClient>(sp =>
    sp.GetRequiredService<WalletSettlementLedgerClient>());

// JEBV4-302: shared per-jeeber earnings-cache invalidation registry. Singleton so the
// read side (JeebEarningsController links each cache entry to the jeeber's change token)
// and the write side (SettlementService trips it when a settlement is recorded) share one
// registry, evicting a pre-settlement cached 0 the moment the jeeber is credited.
builder.Services.AddSingleton<JeebGateway.Financials.IEarningsCacheInvalidator,
    JeebGateway.Financials.EarningsCacheInvalidator>();

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
builder.Services.AddScoped<IFinancialLedgerAnonymizer,
    WalletServiceFinancialLedgerAnonymizer>();

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

// Read-only generic wallet ledger client. Unlike the generated wallet client above, this named
// client only issues GETs, so retry/breaker/timeout resilience is safe. It carries no service-auth
// header: wallet-service is protected by the private overlay/network boundary.
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient(
    JeebGateway.JeebWallet.WalletServiceJeebWalletLedgerReader.HttpClientName,
    client =>
    {
        var apiUrl = builder.Configuration["WalletServiceApi:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
        }
        client.Timeout = TimeSpan.FromSeconds(30);
    }));

// Wallet settlement mutations are idempotent at the owner but are not retried
// automatically after an ambiguous transport failure. A later owner-driven
// reconciliation may replay the stable idempotency key.
ServiceClientExtensions.AttachBreakerAndTimeoutOnly(builder.Services.AddHttpClient(
    JeebGateway.Financials.WalletSettlementLedgerClient.HttpClientName,
    client =>
    {
        var apiUrl = builder.Configuration["WalletServiceApi:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(apiUrl))
        {
            client.BaseAddress = new Uri(apiUrl.TrimEnd('/') + "/");
        }
        client.Timeout = TimeSpan.FromSeconds(30);
    }));

// F1 — offer-submit/accept/edit wallet-sufficiency guard (OQ1 unresolved: see
// WalletGuardOptions). Reuses the ServiceWalletClient registered immediately above.
builder.Services.Configure<JeebGateway.Financials.WalletGuardOptions>(
    builder.Configuration.GetSection(JeebGateway.Financials.WalletGuardOptions.SectionName));
builder.Services.AddScoped<JeebGateway.Financials.IWalletSufficiencyGuard,
    JeebGateway.Financials.WalletSufficiencyGuard>();

// Jeeb Partner Portal wallet BFF (partner-wallet-bff) — validated options + the thin
// saga-orchestration service. Reuses the scoped ServiceWalletClient registered above and the
// IJeebWalletLedgerReader wired below; adds no new HttpClient (all partner money moves flow through
// the same reused wallet-service saga). See Extensions/PartnerWalletExtensions.cs.
builder.Services.AddPartnerWallet(builder.Configuration);

// Wallet-service is the only wallet ledger authority.
builder.Services.AddSingleton<JeebGateway.JeebWallet.WalletServiceJeebWalletLedgerReader>();
builder.Services.AddSingleton<JeebGateway.JeebWallet.IJeebWalletLedgerReader>(sp =>
    sp.GetRequiredService<JeebGateway.JeebWallet.WalletServiceJeebWalletLedgerReader>());

// Notification preferences (T-backend-031 / JEB-1498).
// Wired to the generic remote-user-preferences service (Rust, :10067) so preferences
// survive restarts. Preferences are stored as an opaque JSON blob under key
// "jeeb.notification_prefs" — the shared service learns nothing about Jeeb topics (GR2).
builder.Services.AddSingleton<INotificationPreferencesStore,
    RemoteUserPreferencesNotificationPreferencesStore>();

// WS-02 — Saved Locations BFF (ACCT-04 / REQ-02).
// JEBV4-165 / JEBV4-194 D5 (D1 matrix row 5): saved locations moved off the gateway's
// own Postgres (deleted PostgresSavedLocationStore / saved_locations table) onto its
// owning service, the generic remote-user-preferences service (Rust, :10067) — the same
// GR-2/GR-3-compliant path as notification preferences. The per-user collection is stored
// as one opaque JSON blob under key "jeeb.saved_locations" (the shared service stays
// Jeeb-agnostic). Registered before AddSavedLocations so its test-oriented
// TryAdd fallback is never selected by the production composition root.
builder.Services.AddSingleton<JeebGateway.Users.SavedLocations.ISavedLocationStore,
    JeebGateway.Users.SavedLocations.RemoteUserPreferencesSavedLocationStore>();
builder.Services.AddSavedLocations();

// Legacy gateway call sites retain their IPushNotificationService dependency,
// but the implementation is now a stateless notification-owner adapter. No
// device-token, retry, delivery-tracker, transport, or dispatch-outbox runtime
// is registered in the gateway.
builder.Services.AddSingleton<IPushNotificationService, NotificationOwnerPushService>();
builder.Services.AddSingleton<JeebGateway.Services.Dispatch.INotificationTemplateRenderer,
                               JeebGateway.Services.Dispatch.StaticNotificationTemplateRenderer>();
builder.Services.AddScoped<JeebGateway.Services.Dispatch.IJeebNotificationDispatcher,
                            JeebGateway.Services.Dispatch.JeebNotificationDispatcher>();

// Delivery requests are owned by delivery-service. The compatibility store is
// a stateless adapter; owner capabilities that do not exist fail closed.
builder.Services.Configure<DurableRequestsOptions>(
    builder.Configuration.GetSection(DurableRequestsOptions.SectionName));

// JEB-50 (S05 H7): gateway orchestration for conversation auto-create on order create.
// The provisioner is always registered,
// but it is a no-op that returns null unless FeatureFlags:ConversationAutoCreate
// :Enabled=true — so today's green create path is byte-for-byte unchanged until
// the flag is flipped. It is thin orchestration over the already-registered
// ServiceChatClient (chat-service POST /api/channels), holding no state.
builder.Services.Configure<JeebGateway.Conversations.ConversationProvisionOptions>(
    builder.Configuration.GetSection(JeebGateway.Conversations.ConversationProvisionOptions.SectionName));
// Singleton: the provisioner captures only IServiceScopeFactory (a singleton)
// and opens a fresh scope per call to resolve the scoped ServiceChatClient.
builder.Services.AddSingleton<JeebGateway.Conversations.IConversationProvisioner,
                              JeebGateway.Conversations.ChatServiceConversationProvisioner>();

var bundleBaseUrl = builder.Configuration["JeebStateService:BaseUrl"]
                    ?? builder.Configuration["Services:JeebState:BaseUrl"]
                    ?? string.Empty;
builder.Services.TryAddTransient<JeebGateway.StateService.StateServiceCredentialHandler>();
builder.Services
    .AddHttpClient<JeebGateway.StateService.Durable.ISagaBundleRecorder,
                   JeebGateway.StateService.Durable.StateServiceSagaBundleRecorder>(http =>
    {
        if (!string.IsNullOrWhiteSpace(bundleBaseUrl))
            http.BaseAddress = new Uri(bundleBaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddHttpMessageHandler<JeebGateway.StateService.StateServiceCredentialHandler>()
    .AddStandardResilienceHandler();
builder.Services
    .AddHttpClient<JeebGateway.StateService.Durable.IBroadcastEventRecorder,
                   JeebGateway.StateService.Durable.StateServiceBroadcastEventRecorder>(http =>
    {
        if (!string.IsNullOrWhiteSpace(bundleBaseUrl))
            http.BaseAddress = new Uri(bundleBaseUrl.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddHttpMessageHandler<JeebGateway.StateService.StateServiceCredentialHandler>()
    .AddStandardResilienceHandler();
builder.Services.AddSingleton<IRequestsStore, DeliveryOwnerRequestsStore>();

// S06 (B1/B2/B3/ALT-2/ALT-3/ALT-4/ALT-4b/N5/N6): just-in-time delivery-row
// compatibility call for POST /matching/run. Registered after IRequestsStore
// (which reads from delivery-service) and depends on the
// already-registered IDeliveryServiceClient (idempotent POST /api/v1/deliveries).
// Default-ON (MatchingMirrorOptions.Enabled) to preserve the matching/run
// compatibility contract. Thin BFF orchestration only; instant rollback via
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
// Ban-service is the sole Jeeber restriction owner.
builder.Services.AddSingleton<IJeeberRestrictionStore, BanServiceJeeberRestrictionStore>();
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
builder.Services.AddSingleton<JeebGateway.Ratings.FeedbackServiceRatingStore>();
builder.Services.AddSingleton<IRatingStore>(
    sp => sp.GetRequiredService<JeebGateway.Ratings.FeedbackServiceRatingStore>());
builder.Services.AddSingleton<IRatingStoreExtended,
    JeebGateway.Ratings.UnsupportedUpstreamRatingStoreExtended>();
builder.Services.AddSingleton<IRatingService, RatingService>();

// OTP handover verification + admin escalation (T-backend-015 / JEEB-33).
builder.Services.Configure<OtpHandoverOptions>(builder.Configuration.GetSection(OtpHandoverOptions.SectionName));
builder.Services.AddSingleton<IAdminEscalationStore,
    JeebGateway.Requests.OtpHandover.StateServiceAdminEscalationStore>();

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
else if (isTestHarness)
{
    builder.Services.AddDistributedMemoryCache();
}
else
{
    throw new InvalidOperationException(
        "Redis:ConnectionString is required for cross-replica OTP and handover state.");
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

// Delivery-service is the sole tier-catalog owner. Its current read-only
// contract is projected directly; unsupported admin mutations fail closed.
builder.Services.AddSingleton<JeebGateway.Tiers.ITiersStore,
    JeebGateway.Tiers.DeliveryServiceTiersStore>();

// Request expiry + no-offer nudge (T-backend-028).
builder.Services.Configure<RequestExpiryOptions>(builder.Configuration.GetSection(RequestExpiryOptions.SectionName));
builder.Services.AddSingleton<TierExpiryWindowResolver>();
// P7 (G-J): the read-side offer-wait deadline projection. SINGLETON is required —
// the 60 s tier-catalog cache only caches if the instance survives the request, and
// without it every list/feed read acquires an upstream delivery-service dependency.
builder.Services.AddSingleton<OfferDeadlineProjector>();

// Scheduled-at validation remains a request-bound projection. Activation and
// notification retries execute in delivery-service / notification-service.
builder.Services.Configure<ScheduledDeliveryOptions>(builder.Configuration.GetSection(ScheduledDeliveryOptions.SectionName));

// Prohibited-items catalog + acknowledgements are an unconditional stateless
// projection over ban-service's generic moderation API. The typed owner adapter
// is registered by AddDownstreamClients; no local/Postgres fallback is legal.

// Prohibited-item NLP scanner + admin review queue (T-backend-048).
// The scanner runs Damerau-Levenshtein fuzzy matching with a synonym
// expansion pass against the active catalog. Matches above the review
// threshold are recorded as generic cases in jeeb-state-service; the scanner
// never auto-blocks. Production registers no gateway table or queue fallback.
builder.Services.AddSingleton<IProhibitedItemSynonymRegistry, InMemorySynonymRegistry>();
builder.Services.AddScoped<IProhibitedItemScanner, ProhibitedItemScanner>();

// JEB-63 (S05 N1 / A1.1): gateway-owned create-time prohibited-items moderation
// gate flag (default ON, INDEPENDENT of FeatureFlags:DurableRequests). When ON,
// RequestsController.Create runs the scanner before persisting and hard-rejects
// block-severity / soft-rejects warn-severity items. The gate consumes the
// immutable ban-service-owned catalog snapshot. It runs whether or not
// the durable saga create path is active (the two flags are independent). To
// disable explicitly set FeatureFlags__CreateModeration__Enabled=false.
builder.Services.Configure<JeebGateway.Requests.CreateModerationOptions>(
    builder.Configuration.GetSection(JeebGateway.Requests.CreateModerationOptions.SectionName));

// JEBV4-212 (E17): the shared create-time moderation evaluator. Both the legacy
// RequestsController.Create and the V1 JeebRequestsController.Create (the route the
// mobile app uses) route through this one gate so prohibited-items screening is
// enforced identically on BOTH create paths and can never drift. It is scoped so
// the transient HttpClientFactory owner adapter is never captured for process life.
builder.Services.AddScoped<JeebGateway.Requests.CreateModerationEvaluator>();

// Catalog bootstrap/reconciliation runs in ban-service. An unavailable or
// empty owner catalog is intentionally handled by ModerationGate as fail-closed.

// Administrator actions are persisted by jeeb-state-service's generic,
// append-only audit stream. The adapter is registered with the other state
// owner projections below, after the owner client availability is known.

// Disputes and support are stateless gateway projections over the generic
// jeeb-state-service /v1/cases engine. Evidence is gathered synchronously with
// independent source budgets and explicit partial markers. The gateway owns no
// case database; notifications are driven only by state outbox callbacks.
builder.Services.Configure<CaseEvidenceOptions>(
    builder.Configuration.GetSection(CaseEvidenceOptions.SectionName));
builder.Services.AddScoped<ICaseEvidenceCollector, CaseEvidenceCollector>();
builder.Services.AddScoped<IGenericCaseGatewayService, GenericCaseGatewayService>();

// Legacy dispute fixtures are registered only by the integration-test host.
// The shipped composition exposes the generic state-service case projection.

// COD recording is a wallet-service mutation; no UPG or process-local ledger
// exists in the gateway.
builder.Services.AddSingleton<JeebGateway.Financials.Cod.ICodSettlementLedger,
    JeebGateway.Financials.Cod.WalletCodSettlementLedger>();

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
// CMS/admin user rows are never persisted as a gateway projection. Identity and
// roles come from user-management, suspension from ban-service, and rating
// aggregates from feedback-service on each request.
builder.Services.Configure<JeebGateway.Users.AdminUserBanOptions>(
    builder.Configuration.GetSection(JeebGateway.Users.AdminUserBanOptions.SectionName));
builder.Services.AddScoped<JeebGateway.Users.IAdminUserProjection,
    JeebGateway.Users.OwnerComposedAdminUsers>();
// The broader legacy IUsersStore contract remains as a compatibility facade for
// existing controllers, but its implementation is equally stateless: every method
// routes to UM / ban / feedback / remote-user-preferences. GatewayPostgres and the
// old InMemoryUsersStore are never selected or registered.
builder.Services.AddSingleton<IUsersStore, JeebGateway.Users.OwnerBackedUsersStore>();

// JEBV4-314 — gateway-local, DEV-ONLY bridge from POST /dev/seed/user (role=admin)
// to the POST /v1/auth/login role mint. Always registered but only ever WRITTEN by the
// [DevOnly] SeedUser action (404 unless Features:DevEndpoints:Enabled), so it is empty
// in production and the login consult is a no-op there. See DevSeededRoleStore.
builder.Services.AddSingleton<JeebGateway.Users.IDevSeededRoleStore>(
    isTestHarness || builder.Environment.IsDevelopment()
        ? new JeebGateway.Users.DevSeededRoleStore()
        : new JeebGateway.Users.NoOpDevSeededRoleStore());

// Dual-role identity + BR-1 enforcement (T-backend-041).
// Validates that a user cannot act as both Client and Jeeber simultaneously
// in the same delivery, and that role switches are gated on having no active
// deliveries under the current role.
builder.Services.AddSingleton<IDualRoleService, DualRoleService>();

// Stateless GDPR workflows. State-service owns durable work metadata, claims,
// leases and terminal CAS; private-artifact storage owns export bytes. The
// gateway has no local/Postgres queue and no continuously running hosted worker:
// deployment automation invokes the dedicated service-token endpoints below.
builder.Services.Configure<AccountDeletionExecutionOptions>(
    builder.Configuration.GetSection(AccountDeletionExecutionOptions.SectionName));
builder.Services.Configure<DataExportOptions>(
    builder.Configuration.GetSection(DataExportOptions.SectionName));
builder.Services.Configure<DurableWorkExecutionOptions>(
    builder.Configuration.GetSection(DurableWorkExecutionOptions.SectionName));
builder.Services.Configure<InternalJobAuthOptions>(
    builder.Configuration.GetSection(InternalJobAuthOptions.SectionName));

builder.Services.AddScoped<IAccountDeletionStore, StateServiceAccountDeletionStore>();
builder.Services.AddScoped<InternalJobTokenAuthorizationFilter>();
builder.Services.AddScoped<DurableWorkSweepExecutor>();
builder.Services.AddScoped<IDurableWorkItemHandler, AccountDeletionWorkHandler>();
builder.Services.AddScoped<IDurableWorkItemHandler, DataExportWorkHandler>();

builder.Services.AddSingleton(new PrivateArtifactStoreOptions());
builder.Services.AddHttpClient<IPrivateArtifactStore, PrivateArtifactStoreHttpClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IDataExportTokenProtector, DataExportTokenProtector>();
builder.Services.AddScoped<IDataExportWorkflow, StateDataExportWorkflow>();
builder.Services.AddScoped<IDataExportRatingsProvider, FeedbackServiceDataExportRatingsProvider>();
// Chat history is mandatory for a complete GDPR export. Until chat-service
// publishes member-scoped conversation enumeration, a 404/501 from the live
// index adapter raises an owner-capability error and the durable handler defers.
// The provider pages one stable as_of across that index and the existing bounded
// per-conversation export route.
builder.Services.AddScoped<IChatConversationExportIndex,
    ChatServiceConversationExportIndex>();
builder.Services.AddScoped<IDataExportChatHistoryProvider, ChatServiceDataExportChatHistoryProvider>();
builder.Services.AddScoped<IDataExportNotifier, NotificationOwnerDataExportNotifier>();
builder.Services.AddScoped<IDataExportPackager, DataExportPackager>();

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
builder.Services.AddSingleton<IRefreshTokenStore,
    JeebGateway.Tokens.StateServiceRefreshTokenStore>();
builder.Services.AddSingleton<IUsersStoreAdapter, UsersStoreRolesAdapter>();
builder.Services.AddSingleton<ITokenService, TokenService>();
builder.Services.AddSingleton<IUmAuthenticationContextValidator, UmAuthenticationContextValidator>();

// Admin portal settlement reads/reconcile over the in-gateway COD owner
// (extracted from PR #364; replaces the PR's retired UPG proxy).
builder.Services.AddSingleton<JeebGateway.Financials.IAdminSettlementPortalService,
    JeebGateway.Financials.AdminSettlementPortalService>();

// Admin delivery reads relay to the delivery-service owner contract.
ServiceClientExtensions.AttachResilienceOnly(builder.Services.AddHttpClient("admin-deliveries-owner", client =>
{
    var baseUrl = builder.Configuration["Services:Delivery:BaseUrl"];
    if (Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        client.BaseAddress = new Uri(uri.ToString().TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(8);
})).AddHttpMessageHandler<JeebGateway.Services.Clients.DeliveryServiceCredentialHandler>();

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
    .AddHttpClient<JeebGateway.Users.HttpUserManagementDualRoleClient>(client =>
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

// role-service adapter (net-new, flagged) — single swap point for IUserManagementDualRoleClient.
// Flag off (FeatureFlags:UseUpstream:RoleService): byte-identical to the prior direct registration.
builder.Services.Configure<JeebGateway.Services.Clients.RoleServiceOptions>(
    builder.Configuration.GetSection(JeebGateway.Services.Clients.RoleServiceOptions.SectionName));
builder.Services.AddScoped<JeebGateway.Users.IUserManagementDualRoleClient>(sp =>
    new JeebGateway.Users.RoleServiceBackedDualRoleClient(
        sp.GetRequiredService<JeebGateway.Users.HttpUserManagementDualRoleClient>(),
        sp.GetRequiredService<JeebGateway.Services.Clients.IRoleServiceClient>(),
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<JeebGateway.Services.UpstreamFeatureFlags>>(),
        sp.GetRequiredService<ILogger<JeebGateway.Users.RoleServiceBackedDualRoleClient>>()));

// Jeeber availability projection (T-backend-023). Delivery-service owns the
// availability record; the gateway retains only a rebuildable geo index.
builder.Services.Configure<AutoOfflineOptions>(builder.Configuration.GetSection(AutoOfflineOptions.SectionName));

// Admin ops-map zone grouping (T-backend-051). Boundaries are
// reloaded on config change via IOptionsMonitor so operators can
// re-shape coverage without redeploying the gateway.
builder.Services.Configure<ZoneOptions>(builder.Configuration.GetSection(ZoneOptions.SectionName));
// IGeoIndex is INTENTIONALLY in-memory (JEBV4-156) — it is a DERIVED, rebuildable
// hot-path spatial index, NOT a store of record. Delivery-service owns online
// presence and vehicle/last-seen data. Losing this derived index costs only a
// warm-up and never loses authoritative state.
// Tracked as IntentionalInMemory (not the migration backlog) in StoreDurabilityGuard.
builder.Services.AddSingleton<IGeoIndex, InMemoryGeoIndex>();

// Offer record-of-truth (T-backend-010). thin-BFF wire: the offer ledger is the
// real offer-service (Elixir/Phoenix, host port 10063), proxied via
// UpstreamPendingOffersStore → IOfferServiceClient. The gateway holds NO offer
// state.
//
// GW3 / W3.5(c) — what this block used to say, and why it was wrong.
// It used to register an in-memory offer store as a concrete singleton alongside
// this mapping, select it whenever FeatureFlags:UseUpstream:Offer was false, and
// justify keeping it with:
//
//     "The in-memory store is KEPT registered either way so existing fixtures and
//      the auto-offline sweeper / accept-lookup paths (which offer-service has no
//      read route for yet) continue to resolve it directly"
//
// The second half of that sentence was FALSE, and had been for as long as the
// comment existed. Grepping the concrete store type across src/ returned exactly
// SEVEN hits: the class itself (2), two comments in sibling files, and these
// registration lines (3). ZERO concrete injections anywhere else. The auto-offline
// sweeper (PostgresAvailabilityStore / InMemoryAvailabilityStore) and the
// accept-lookup path (OffersController / JeebOffersController) all take
// IPendingOffersStore, so at Offer=true — which is what every deployed overlay
// sets — they resolved the UPSTREAM store and hit its NotSupportedException on
// GetAsync / WithdrawForJeeberAsync. They never once "resolved it directly".
// A comment that names a defence which does not exist is worse than no comment:
// it retires the question.
//
// The store itself was fixture machinery (it shipped an `EnqueueForTest` seam in
// production source and called itself "MVP"), so it moved into the test project as
// JeebGateway.IntegrationTests.Fakes.FakePendingOffersStore (git mv, history kept)
// and this mapping became unconditional.
//
// CONSEQUENCE, stated so nobody rediscovers it as a bug: FeatureFlags:UseUpstream:Offer
// = false no longer selects a second store — there is no second store. The flag still
// gates the ONE remaining legacy offer branch (OffersController's inline accept), which
// is self-labelled test-only and now runs only against a store a TEST supplies.
// (EditInMemoryAsync used to be named here too; it was deleted 2026-08-01 — it had been
// unreachable, its !_flags.Offer branch returning 503 rather than calling it.) Off + no override = the offer surface is not functional. Every
// deployed overlay sets it true (appsettings.Production.json); the base false is the
// test-harness default. Finishing the migration properly — so the flag can be deleted —
// needs offer-service to grow get-by-id and bulk-withdraw routes (JEBV4-148), which is
// an owner / service-owner call, not a gateway change.
builder.Services.AddSingleton<IPendingOffersStore>(sp =>
    new UpstreamPendingOffersStore(
        sp.GetRequiredService<JeebGateway.Services.Clients.IOfferServiceClient>(),
        sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
        // fix/offer-visibility (run-23 CHECK C): the submit-time routing index + the
        // request read-model let ListForJeeberAsync recover the jeeber's own (incl.
        // TERMINAL) offers through the owner-scoped request-list route — offer-service
        // exposes no jeeber-scoped list route.
        sp.GetRequiredService<IOfferRequestIndex>(),
        sp.GetRequiredService<JeebGateway.Requests.IRequestsStore>()));
// Offer → request routing index (S07 accept saga). Records the immutable
// offerId → requestId pairing at submit time so the offer-scoped accept route
// (POST /v1/offers/{id}/accept) can forward to the request-scoped offer-service
// accept saga under FeatureFlags:UseUpstream:Offer. Routing concern only — no
// auction domain state lives here.
// Registered after the state owner is wired below. There is no local cache or
// fallback: replica-independent reads always go to jeeb-state-service.
// GW3 / W3.5(a) — the "new offer" realtime fan-out seam is DELETED, not rewired.
// It was an in-process, in-memory event recorder registered here
// unconditionally and injected into the LIVE RequestOffersController, so every
// real jeeber offer appended an event to a List<T> that nothing ever read: no
// SignalR hub, no realtime-service client, no reader of any kind. A seam that
// only pretends to notify is worse than an absent one, because the controller's
// own doc claimed the Client received a WS event on every submission.
// The customer IS notified on this path — by IOfferPushNotifier, through the
// push microservice (see RequestOffersController.Submit, BUILD-OFFER-PUSH).
// If a genuine realtime fan-out is wanted later, add it then; do not re-add a
// recorder to hold its place.

// Auto-offline notifications flow through the shared push pipeline
// (T-backend-022, T-backend-023) so they obey the same transport and retry
// rules as any other trigger.
builder.Services.AddSingleton<IAutoOfflineNotifier, PushAutoOfflineNotifier>();
builder.Services.AddSingleton<IAvailabilityStore,
    JeebGateway.Availability.DeliveryServiceAvailabilityStore>();

// F3 (unregister-as-jeeber) guard 3 — forces presence offline through whichever
// path is authoritative, same branch AvailabilityController itself uses.
builder.Services.AddSingleton<JeebGateway.Users.IJeeberForceOfflineOnUnregister,
    JeebGateway.Users.JeeberForceOfflineOnUnregister>();

// GPS location tracking (T-backend-014).
// The store is an in-memory ConcurrentDictionary keyed by Jeeber id.
//
// PRODUCTION REDIS SWAP — read before changing either number:
//   SET jeeber:{id}:position <json incl. receivedAt> EX <Tracking:PositionRetention>
// The EX is Tracking:PositionRetention (default 43200 s), NOT Tracking:PositionTtl.
// Key expiry is a MEMORY bound; freshness is derived at read time from the
// receivedAt stamp inside the value. The old design used `EX 300` and let key
// absence mean "stale", which made "the courier never started" and "we have lost
// the courier" identical on the wire — the phantom courier pin. Setting EX back to
// PositionTtl would reintroduce that defect on the Redis path only, invisibly, and
// only in production.
//
// The three windows are a strict ladder: 0 < StaleThreshold <= PositionTtl <
// PositionRetention. ValidateOnStart refuses to boot on a config that breaks it,
// because every ordering violation silently degrades or erases a wire state:
// StaleThreshold > PositionTtl skips "stale" entirely, and PositionRetention <=
// PositionTtl erases "lost" by forgetting the fix before it can be reported.
builder.Services
    .AddOptions<TrackingOptions>()
    .Bind(builder.Configuration.GetSection(TrackingOptions.SectionName))
    .Validate(
        o => o.StaleThreshold > TimeSpan.Zero,
        "Tracking:StaleThreshold must be greater than zero.")
    .Validate(
        o => o.PositionTtl >= o.StaleThreshold,
        "Tracking:PositionTtl must be >= Tracking:StaleThreshold, otherwise a position can never be reported as 'stale' — it jumps straight from 'live' to 'lost'.")
    .Validate(
        o => o.PositionRetention > o.PositionTtl,
        "Tracking:PositionRetention must be > Tracking:PositionTtl, otherwise a fix is forgotten before it can be reported as 'lost' and a missing courier becomes indistinguishable from one who never started (the phantom-pin defect).")
    .Validate(
        o => o.MaxPointsPerBatch > 0,
        "Tracking:MaxPointsPerBatch must be greater than zero.")
    .ValidateOnStart();
// Gap 1 flag-gated store swap (BanService precedent): when
// FeatureFlags:UseUpstream:Geolocation is ON, the record-of-truth is the shared
// geolocation-service via GeoServiceLocationStore (NSwag client); default OFF keeps
// the in-memory store so neither the controller nor the SSE loop branch on the flag.
// JEBV4-57 (GW12-PERF-1): flipping this flag ON is now SAFE — ILocationStore is
// async end-to-end, so GeoServiceLocationStore awaits the geolocation-service client
// with NO sync-over-async bridge. There is no longer a blocking hot path that a GPS
// fan-out storm could use to starve the shared ASP.NET thread pool.
builder.Services.AddSingleton<ILocationStore, JeebGateway.Tracking.GeoServiceLocationStore>();
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

// Weekly settlement batches remain an owner capability gate until wallet-service
// publishes that API. No gateway batch runner is registered.
builder.Services.AddSingleton<JeebGateway.Financials.ISettlementBatchStore,
    JeebGateway.Financials.UnavailableSettlementBatchStore>();

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
builder.Services.AddScoped<JeebGateway.Financials.IEarningsAggregationService,
    JeebGateway.Financials.WalletEarningsAggregationService>();

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
builder.Services.AddScoped<JeebGateway.Financials.QuestPdfEarningsStatementGenerator>();
builder.Services.AddScoped<JeebGateway.Financials.IEarningsPdfGenerator>(sp =>
    new JeebGateway.Financials.CachedEarningsPdfGenerator(
        sp.GetRequiredService<JeebGateway.Financials.QuestPdfEarningsStatementGenerator>(),
        sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
        sp.GetRequiredService<TimeProvider>()));

// T-backend-033: Admin finance dashboard API.
builder.Services.AddSingleton<JeebGateway.Financials.IAdminFinanceDashboardService, JeebGateway.Financials.AdminFinanceDashboardService>();

// Rating reveal and low-rating policy execution belong to feedback-service.
// The gateway registers no rating cron job or notification worker.

// T-backend-037: Chat data retention is now a chat-service concern.
// The in-gateway retention sweeper + in-memory retention store have been DELETED:
// the gateway holds no chat record-of-truth, so it cannot (and must not) purge
// messages. Retention/TTL belongs to the owning chat-service.

// T-backend-044: Masked phone calls via Twilio proxy (Phase 2).
builder.Services.Configure<JeebGateway.Calls.MaskedCallOptions>(
    builder.Configuration.GetSection(JeebGateway.Calls.MaskedCallOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Calls.IMaskedCallService, JeebGateway.Calls.MaskedCallService>();

// Speech-to-text ownership is entirely in voice-transcription-service. The
// typed downstream client and aggregate /healthz probe are registered in the
// shared BFF extensions; the gateway registers no Whisper client, circuit,
// audio store, fallback provider, retry queue, or worker.

// Readiness surface for the owner-only stateless composition gate. Production
// reports 503 for a missing/incorrect adapter or owner credential; explicit
// Development/Testing hosts remain exempt.
builder.Services.AddHealthChecks()
    .AddCheck<JeebGateway.Infrastructure.StoreDurabilityHealthCheck>("store-durability", tags: new[] { "ready" });

// ---------------------------------------------------------------------------
// jeeb-state-service owner rewire (ADR-001-rev2, Layer-2 R1–R8).
//
// Generic cases are always persisted by jeeb-state-service. When that service
// is not configured, case routes return 503 rather than creating local state.
// No production route has a local state fallback.
// ---------------------------------------------------------------------------
var stateOptions = new JeebGateway.StateService.StateServiceOptions
{
    BaseUrl = builder.Configuration["JeebStateService:BaseUrl"]
              ?? builder.Configuration["Services:JeebState:BaseUrl"]
              ?? string.Empty,
    TimeoutSeconds = int.TryParse(builder.Configuration["JeebStateService:TimeoutSeconds"], out var ts) ? ts : 5,
    ServiceTokenFile = builder.Configuration["JeebStateService:ServiceTokenFile"] ?? string.Empty,
    Enabled = !bool.TryParse(builder.Configuration["JeebStateService:Enabled"], out var en) || en
};
var stateServiceWired = stateOptions.Enabled && !string.IsNullOrWhiteSpace(stateOptions.BaseUrl);
builder.Services.AddSingleton(stateOptions);
if (stateServiceWired)
{
    builder.Services.AddJeebStateServiceClient(stateOptions);
    builder.Services.AddTransient<IGenericCaseStateClient>(services =>
        (IGenericCaseStateClient)services.GetRequiredService<IJeebStateServiceClient>());

    // R1 — idempotency (full 1:1; GET-by-key ⇒ bounce-survivable).
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore>();
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IExternalIdempotencyStore>(sp =>
        sp.GetRequiredService<JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore>());
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IIdempotencyStore>(sp =>
        sp.GetRequiredService<JeebGateway.StateService.Idempotency.StateServiceIdempotencyStore>());

    // S08 (A3/N9) — owner-backed offer→request routing. The immutable
    // offerId → (requestId, jeeberId) pairing survives a gateway bounce and is
    // shared across replicas through jeeb-state-service.
    builder.Services.AddSingleton<IOfferRequestIndex,
        JeebGateway.StateService.Durable.StateServiceOfferRequestIndex>();

    // R8 — rate-limit + handover locks (keyed by bucket/lockKey ⇒ bounce-survivable).
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateRateLimitStore,
        JeebGateway.StateService.RateLimiting.StateServiceRateLimitStore>();
    builder.Services.AddSingleton<JeebGateway.StateService.RateLimiting.IStateLockStore,
        JeebGateway.StateService.RateLimiting.StateServiceLockStore>();

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
else
{
    builder.Services.AddSingleton<IGenericCaseStateClient, UnavailableGenericCaseStateClient>();
    builder.Services.AddSingleton<JeebGateway.StateService.Work.IStateWorkItemClient,
        JeebGateway.StateService.Work.UnavailableStateWorkItemClient>();
    builder.Services.AddSingleton<JeebGateway.StateService.Audit.IStateAuditClient,
        JeebGateway.StateService.Audit.UnavailableStateAuditClient>();
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.UnavailableIdempotencyStore>();
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IExternalIdempotencyStore>(sp =>
        sp.GetRequiredService<JeebGateway.StateService.Idempotency.UnavailableIdempotencyStore>());
    builder.Services.AddSingleton<JeebGateway.StateService.Idempotency.IIdempotencyStore>(sp =>
        sp.GetRequiredService<JeebGateway.StateService.Idempotency.UnavailableIdempotencyStore>());
}

builder.Services.AddScoped<IAdminAuditLog, JeebGateway.Admin.StateServiceAdminAuditLog>();
builder.Services.AddScoped<IFlaggedRequestStore,
    JeebGateway.ProhibitedItems.FlaggedRequests.StateServiceFlaggedRequestStore>();

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

// Fails closed at startup when the external administrator identity is switched
// on but incomplete; a disabled AdminOidc section keeps the live BFF untouched.
JeebGateway.Auth.Oidc.AdminOidcStartupGuard.EnsureConfigured(
    app.Configuration, app.Environment);

if (app.Configuration.GetValue<bool>("FeatureFlags:UseUpstream:Ratings"))
{
    app.Logger.LogCritical(
        "FeatureFlags:UseUpstream:Ratings is ON, but feedback-service does not expose list-expired-windows or mark-revealed/closed rating APIs; the gateway will not fabricate upstream reveal state.");
}

// Fail-closed stateless boundary. Production refuses to boot unless every
// critical state contract resolves to its explicit owner adapter, owner secrets
// are mounted, no DB/UPG configuration exists, and no gateway state worker is
// registered. Development/Testing are explicit fixture environments.
JeebGateway.Infrastructure.StoreDurabilityGuard.EnsureDurable(
    app.Services, app.Environment,
    app.Services.GetRequiredService<ILogger<Program>>());

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

// OpenAPI.NET omits an empty Security collection. Preserve the operation
// filter's explicit anonymous semantics as `security: []` on the wire.
app.UseMiddleware<JeebGateway.OpenApi.ExplicitAnonymousOpenApiSecurityMiddleware>();

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

app.Run();

// Required for WebApplicationFactory<Program> integration tests.
public partial class Program { }
