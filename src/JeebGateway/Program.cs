using System.Text;
using JeebGateway.Admin;
using JeebGateway.Availability;
using JeebGateway.Chat;
using JeebGateway.Kyc;
using JeebGateway.Matching;
using JeebGateway.Middleware;
using JeebGateway.NotificationPreferences;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------

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
builder.Services.AddSignalR();
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

// Health checks
builder.Services.AddHealthChecks();

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
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
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
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    });

// Typed HttpClient registrations for downstream services will go here.
// Example:
// builder.Services.AddHttpClient<ISomeServiceClient, SomeServiceClient>();

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

// Geo-matching engine (T-backend-008).
// Queries online Jeebers within the tier-specific radius (PostGIS-style
// ST_DWithin, MVP-side using Haversine), filters by vehicle-type
// compatibility, orders by proximity-then-rating, and fans out a
// "new offer" push to the matched set under a single 2-second deadline.
// Production wiring swaps:
//   - InMemoryJeeberRatingProvider → ratings-service NSwag client.
//   - The candidate scan moves to a PostGIS ST_DWithin query against
//     the GEOGRAPHY(Point, 4326) column on jeeber_availability.
builder.Services.Configure<MatchingOptions>(builder.Configuration.GetSection(MatchingOptions.SectionName));
builder.Services.AddSingleton<InMemoryJeeberRatingProvider>();
builder.Services.AddSingleton<IJeeberRatingProvider>(sp => sp.GetRequiredService<InMemoryJeeberRatingProvider>());
builder.Services.AddSingleton<IMatchingService, MatchingService>();

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
builder.Services.AddSingleton<InMemoryDataExportChatHistoryProvider>();
builder.Services.AddSingleton<IDataExportChatHistoryProvider>(sp => sp.GetRequiredService<InMemoryDataExportChatHistoryProvider>());
builder.Services.AddSingleton<InMemoryDataExportNotifier>();
builder.Services.AddSingleton<IDataExportNotifier>(sp => sp.GetRequiredService<InMemoryDataExportNotifier>());
builder.Services.AddSingleton<IDataExportPackager, DataExportPackager>();
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
builder.Services.AddSingleton<InMemoryPendingOffersStore>();
builder.Services.AddSingleton<IPendingOffersStore>(sp => sp.GetRequiredService<InMemoryPendingOffersStore>());
builder.Services.AddSingleton<InMemoryAutoOfflineNotifier>();
builder.Services.AddSingleton<IAutoOfflineNotifier>(sp => sp.GetRequiredService<InMemoryAutoOfflineNotifier>());
builder.Services.AddSingleton<IAvailabilityStore, InMemoryAvailabilityStore>();
builder.Services.AddHostedService<AutoOfflineSweeper>();

// Real-time chat (T-backend-012).
// SignalR hub at /hubs/chat delivers each message under the 1s WS SLA
// to the conversation group; backgrounded recipients (no live hub
// connection or client-reported background state) fall back to the
// T-backend-022 push pipeline. In-memory stores for the MVP — the
// production swap moves persistence to Postgres and presence to Redis,
// proxied through an NSwag-generated chat-service client per the BFF
// aggregation policy.
builder.Services.AddSingleton<IChatMessageStore, InMemoryChatMessageStore>();
builder.Services.AddSingleton<IChatPresenceTracker, InMemoryChatPresenceTracker>();
builder.Services.AddSingleton<IChatDispatcher, ChatDispatcher>();

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
// Middleware pipeline
// ---------------------------------------------------------------------------

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<RequestValidationMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<ApiKeyAuthenticationMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "Jeeb Gateway v1"));
}

app.UseRouting();

// CORS must run after UseRouting so endpoint-specific CORS metadata applies,
// and before UseAuthentication so preflight requests are not rejected as 401.
var corsPolicyName = (builder.Configuration.GetSection(SecurityOptions.SectionName)
    .Get<SecurityOptions>() ?? new SecurityOptions()).Cors.PolicyName;
app.UseCors(corsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

// T-backend-041: inject the user's persisted active role into HttpContext.Items
// and reject tokens with a stale active_role claim.
app.UseMiddleware<ActiveRoleMiddleware>();

// Rate limiter must run after authentication so the per-user partition can
// read the JWT sub claim.
app.UseRateLimiter();

app.MapControllers();

// Real-time chat WebSocket surface (T-backend-012).
app.MapHub<ChatHub>("/hubs/chat");

// Health endpoints — separate liveness and readiness probes.
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false // liveness: always 200 if process is up
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.Run();

// Required for WebApplicationFactory<Program> integration tests.
public partial class Program { }
