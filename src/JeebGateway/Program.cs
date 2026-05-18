using System.Text;
using JeebGateway.Admin;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Availability;
using JeebGateway.Chat;
using JeebGateway.Disputes;
using JeebGateway.Extensions;
using JeebGateway.Financials;
using JeebGateway.Kyc;
using JeebGateway.Matching;
using JeebGateway.Middleware;
using JeebGateway.NotificationPreferences;
using JeebGateway.ProhibitedItems;
using JeebGateway.Ratings;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Push;
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
using JeebGateway.Wallet;
using JeebGateway.Whisper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
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

// Health checks — the live probe ("self") returns 200 if the process is up;
// downstream-service probes are wired below via AddDownstreamHealthChecks and
// only run under the readiness predicate.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy("process alive"), tags: new[] { "live" });

// ---------------------------------------------------------------------------
// BFF aggregation skeleton (T-migrate-gateway-shell)
//
// AddDownstreamClients registers a named HttpClient + Polly resilience
// pipeline (retry + circuit breaker + per-attempt timeout) per upstream
// service. Generated NSwag typed clients (Services/Generated/*Client.cs) hang
// off these named registrations once each per-controller migration ticket
// lands. See Extensions/ServiceClientExtensions.cs and
// scripts/regenerate-clients.sh.
//
// AddDownstreamHealthChecks registers a /health URL-group probe per upstream
// (tagged "ready" + "downstream", failureStatus: Degraded). Unset BaseUrls
// silently skip — local dev does not have to spin up every backend.
// ---------------------------------------------------------------------------
builder.Services.AddDownstreamClients(builder.Configuration);
builder.Services.AddDownstreamHealthChecks(builder.Configuration);

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
            // T-BE-001 / JEB-471 — OTP sign-in spans
            // (auth.otp.request / auth.otp.verify / auth.refresh).
            .AddSource(OtpSignInActivitySource.Name)
            .AddOtlpExporter(opt => opt.Endpoint = new Uri(otlpEndpoint));
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            // T-backend-050 — Jeeb-owned per-endpoint latency meter.
            .AddMeter(RequestLatencyMetrics.MeterName)
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
// ledger entry to wallet-service via WalletServiceClient. The in-memory
// fallback for IWalletServiceClient lets MVP / integration tests run
// without a downstream wallet instance — the HTTP-backed client takes
// over when Services:Wallet:BaseUrl is configured. The settlement store
// stays in-memory pending the T-backend-bff-wallet migration to the
// generated wallet-service client.
builder.Services.AddSingleton<ISettlementStore, InMemorySettlementStore>();
builder.Services.AddSingleton<InMemoryWalletServiceClient>();
var walletBaseUrl = builder.Configuration["Services:Wallet:BaseUrl"]
    ?? builder.Configuration["Services:Wallet"];
if (!string.IsNullOrWhiteSpace(walletBaseUrl))
{
    builder.Services.AddHttpClient<IWalletServiceClient, WalletServiceClient>(http =>
    {
        http.BaseAddress = new Uri(walletBaseUrl!.TrimEnd('/') + "/");
        http.Timeout = TimeSpan.FromSeconds(30);
    });
}
else
{
    builder.Services.AddSingleton<IWalletServiceClient>(sp =>
        sp.GetRequiredService<InMemoryWalletServiceClient>());
}
builder.Services.AddSingleton<ISettlementService, SettlementService>();

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

// Tier-existence probe consumed by RequestsController to enforce
// T-backend-007's "validate tier exists" criterion. Distinct interface
// from JeebGateway.Tiers.ITiersStore (the admin/catalog surface).
builder.Services.AddSingleton<JeebGateway.Requests.ITiersStore, JeebGateway.Requests.InMemoryTiersStore>();

// Delivery cancellation pipeline (T-backend-024 / JEEB-42).
builder.Services.AddSingleton<IJeeberRestrictionStore, InMemoryJeeberRestrictionStore>();
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

// ===========================================================================
// T-BE-001 / JEB-471 — OTP sign-in via olivium-dev/one-time-password
// (Twilio) + olivium-dev/user-management (sibling T-BE-001a).
//
// Registers:
//   - JeebJwtOptions / GatewayRateLimitOptions / UserManagementApiOptions /
//     ServiceOtpApiOptions  (Options pattern + ValidateOnStart)
//   - IPhoneNormalizer (libphonenumber-csharp, region=LB)
//   - IPhoneHasher (BCrypt workFactor=12)
//   - IOtpRequestRateLimiter (sliding-minute 10/IP + 3/phone)
//   - IRefreshTokenFamilyStore (in-memory; production swap → Postgres)
//   - IJeebJwtIssuer (HS512, access 1h, refresh 30d, family rotation)
//   - IUserManagementPhoneIdentityClient (fail-closed shim until T-BE-001a)
//
// AuthOtpController routes:
//   POST /v1/auth/otp/request
//   POST /v1/auth/otp/verify
//   POST /v1/auth/refresh
//
// Frozen ProblemDetails type set (AC-ProblemTypeSet):
//   invalid_otp, too_many_attempts, invalid_country, rate_limited, invalid_phone
// ===========================================================================
builder.Services.AddJeebOtpSignIn(builder.Configuration);

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

// T-backend-037: Chat data retention and cleanup job.
builder.Services.Configure<JeebGateway.Chat.ChatRetentionOptions>(
    builder.Configuration.GetSection(JeebGateway.Chat.ChatRetentionOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Chat.IChatRetentionStore, JeebGateway.Chat.InMemoryChatRetentionStore>();
builder.Services.AddHostedService<JeebGateway.Chat.ChatRetentionSweeper>();

// T-backend-044: Masked phone calls via Twilio proxy (Phase 2).
builder.Services.Configure<JeebGateway.Calls.MaskedCallOptions>(
    builder.Configuration.GetSection(JeebGateway.Calls.MaskedCallOptions.SectionName));
builder.Services.AddSingleton<JeebGateway.Calls.IMaskedCallService, JeebGateway.Calls.MaskedCallService>();

// T-backend-045: In-app wallet and top-up (Phase 2).
builder.Services.AddSingleton<JeebGateway.Wallet.IInAppWalletService, JeebGateway.Wallet.InAppWalletService>();

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

// T-backend-041: inject the user's persisted active role into HttpContext.Items
// and reject tokens with a stale active_role claim.
app.UseMiddleware<ActiveRoleMiddleware>();

// Rate limiter must run after authentication so the per-user partition can
// read the JWT sub claim.
app.UseRateLimiter();

app.MapControllers();

// T-backend-050 — Prometheus scrape endpoint. Returns the OpenMetrics
// snapshot for the configured MeterProvider (ASP.NET Core HTTP server,
// HttpClient, and the Jeeb-owned RequestLatencyMetrics histogram).
app.MapPrometheusScrapingEndpoint("/metrics");

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
