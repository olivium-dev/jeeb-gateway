using System.Threading;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using StackExchange.Redis;

namespace JeebGateway.Extensions;

/// <summary>
/// Registers <c>HttpClient</c> instances for every upstream service the BFF
/// aggregates. The org-standard resilience pipeline
/// (<see cref="HttpRetryStrategyOptions"/>, <see cref="HttpCircuitBreakerStrategyOptions"/>,
/// <see cref="HttpTimeoutStrategyOptions"/>) is applied uniformly so callers
/// never have to remember to wrap individual calls.
///
/// The named clients registered here ("auth", "chat", ...) are the integration
/// points consumed by NSwag-generated typed clients (see
/// <see cref="Services/Generated"/>).
///
/// IMPORTANT: a typed <c>AddHttpClient&lt;IFooClient, FooClient&gt;</c>
/// registration does NOT inherit a separately-registered NAMED client's handler
/// chain — <see cref="IHttpClientFactory"/> keys handler chains by client name,
/// and a typed registration uses the type name as its key. Each typed client
/// therefore gets its OWN handler chain and must have the bearer-forwarding,
/// X-Service-Auth signing, and resilience handlers attached explicitly. The
/// <see cref="AttachStandardPipeline"/> helper does exactly that, so every
/// post-auth typed client below carries the same cross-cutting behaviour as the
/// named clients. (Pre-auth OTP sign-in clients are registered separately in
/// <c>Auth/OtpSignIn/OtpSignInServiceCollectionExtensions.cs</c> and must NOT
/// carry bearer/ServiceAuth headers — they run before a caller token exists.)
///
/// This is the BFF gateway aggregation pattern; see
/// <c>olivium-domain-atlas</c> entries for rahmah-gateway, salehly-gateway,
/// cremat.
/// </summary>
public static class ServiceClientExtensions
{
    /// <summary>
    /// Registers a named <see cref="HttpClient"/> per upstream Jeeb service
    /// with consistent base-address binding and a standard resilience pipeline.
    /// </summary>
    public static IServiceCollection AddDownstreamClients(
        this IServiceCollection services,
        IConfiguration config)
    {
        // JEB-67 / T-BE-031 — DelegatingHandlers attached to every named
        // downstream client below. BearerForwardingHandler propagates the
        // inbound mobile JWT (AC3 — bearer forwarded); ServiceAuthSigningHandler
        // attaches the X-Service-Auth HMAC (AC3 — ServiceAuth signed). Both
        // are transient because DelegatingHandler is captured per request by
        // HttpClientFactory.
        services.AddHttpContextAccessor();
        services.AddTransient<BearerForwardingHandler>();
        services.AddTransient<ServiceAuthSigningHandler>();

        // TODO(T-backend-bff-auth): auth-service — wire NSwag-generated AuthServiceClient
        //   contract: src/JeebGateway/contracts/auth-service.openapi.json
        //   migrates: AuthController, TokensController (currently in-memory)
        AddNamedDownstreamClient(services, config, "auth", "Services:Auth:BaseUrl");

        // chat-service — GENERIC member/channel/session/message API. The Jeeb
        //   1:1 conversation aggregation lives in ChatServiceClient (BFF), which
        //   calls only the generic routes below. No product-specific chat surface
        //   exists on the shared chat-service.
        //   migrates: ChatController (REST send/history) + SignalR fan-out.
        AddNamedDownstreamClient(services, config, "chat", "Services:Chat:BaseUrl");

        // TODO(T-backend-bff-user): user-management — wire NSwag-generated UserManagementClient
        //   contract: src/JeebGateway/contracts/user-management.openapi.json
        //   migrates: UsersController, AdminUsersController (currently InMemoryUsersStore)
        AddNamedDownstreamClient(services, config, "user-management", "Services:UserManagement:BaseUrl");

        // wallet-service is wired in Program.cs as a salehly-mirrored named
        // IHttpClientFactory client ("ServiceWalletClient" bound to
        // WalletServiceApi:BaseUrl) + scoped ServiceWalletClient typed client,
        // not via this generic named-downstream helper.

        // matching (FastAPI) — DB-backed read of a user's match preferences
        //   (GET /api/v1/matches/{user_id}), consumed by MatchingController's
        //   GetMatchingUsers. Courier matching /run was relocated to
        //   delivery-service; see IDeliveryServiceClient.RunMatchingAsync.
        AddNamedDownstreamClient(services, config, "matching", "Services:Matching:BaseUrl");

        // TODO(T-backend-bff-notification): notification-service (FastAPI) — wire NotificationServiceClient
        //   contract: src/JeebGateway/contracts/notification-service.openapi.json
        //   migrates: NotificationPreferencesController, request-expiry notifier targets
        AddNamedDownstreamClient(services, config, "notification", "Services:Notification:BaseUrl");

        // TODO(T-backend-bff-geo): geolocation-service (FastAPI) — wire GeolocationServiceClient
        //   contract: src/JeebGateway/contracts/geolocation-service.openapi.json
        //   migrates: LocationController, AdminZonesController (currently InMemoryLocationStore + InMemoryGeoIndex)
        AddNamedDownstreamClient(services, config, "geolocation", "Services:Geolocation:BaseUrl");

        // TODO(T-backend-bff-push): push-notification (FastAPI) — wire PushNotificationClient
        //   contract: src/JeebGateway/contracts/push-notification.openapi.json
        //   migrates: PushController (currently FcmPushTransport + InMemoryPushTransport)
        AddNamedDownstreamClient(services, config, "push-notification", "Services:PushNotification:BaseUrl");

        // TODO(T-backend-bff-delivery): delivery-service (Go) — wire DeliveryServiceClient
        //   contract: src/JeebGateway/contracts/delivery-service.openapi.json
        //   migrates: DeliveriesController, RequestsController, RequestOffersController,
        //             OffersController, CancellationController, OtpHandoverController
        //             (currently IRequestsStore + InMemoryRequestsStore)
        AddNamedDownstreamClient(services, config, "delivery", "Services:Delivery:BaseUrl");

        // T-backend-020 (JEEB-38): score-taking-service captures the canonical
        // per-party rating once the gateway's mutual-blind state machine
        // accepts it. Reveal logic remains in JeebGateway.Ratings.
        AddNamedDownstreamClient(services, config, "score-taking", "Services:ScoreTaking:BaseUrl");

        // T-BE-019 (JEB-55): one-time-password service for delivery handover OTP
        // ApplicationId pattern: delivery_handover_{deliveryId}
        AddNamedDownstreamClient(services, config, "otp", "Services:ServiceOTP:BaseUrl");

        // remote-user-preferences — the fleet-wide user-preference store
        // (internal port 10023; jeeb host port 10067). EXACT-SALEHLY MIRROR: the
        // typed BFF client (IUserPreferencesClient) was removed in favour of the
        // NSwag-generated salehly client ServiceRemoteUserPreferencesClient, which
        // is registered as a SCOPED named client in Program.cs against the
        // RemoteUserPreferencesServiceApi:BaseUrl config key (salehly's key). This
        // named registration carries BaseAddress + the standard resilience
        // pipeline so that scoped client inherits the org-standard outbound chain.
        AddNamedDownstreamClient(services, config, "remote-user-preferences", "RemoteUserPreferencesServiceApi:BaseUrl");

        // T-migrate-gateway-proxies (PR-A): typed clients for every post-auth
        // upstream. Each is registered with its OWN handler chain — bearer
        // forwarding + X-Service-Auth signing + the org-standard resilience
        // pipeline — via AttachStandardPipeline. (A typed registration does not
        // inherit the same-named named-client's handlers; see the type doc.)
        // Each controller checks the matching FeatureFlags:UseUpstream:* flag
        // and falls back to the legacy in-memory implementation when false.
        AttachStandardPipeline(
            services.AddHttpClient<IAuthServiceClient, AuthServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Auth")));
        AttachStandardPipeline(
            services.AddHttpClient<IDeliveryServiceClient, DeliveryServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Delivery")));
        AttachStandardPipeline(
            services.AddHttpClient<IMatchingServiceClient, MatchingServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Matching")));
        AttachStandardPipeline(
            services.AddHttpClient<IGeolocationServiceClient, GeolocationServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Geolocation")));

        // Chat BFF facade over the GENERIC chat-service (Firestore-backed, C#/.NET 8,
        // Services:Chat:BaseUrl). ChatServiceClient performs the Jeeb 1:1 aggregation
        // entirely in the gateway, calling only the generic primitives:
        //   POST /api/members
        //   POST /api/channels
        //   POST /api/channels/{channelId}/members   (returns a session id)
        //   POST /api/channels/{channelId}/messages  (requires a valid session)
        //   GET  /api/channels/{channelId}/messages/{messageId}
        //   GET  /api/channels/{channelId}/summary
        // It never calls any product-specific chat route. The named "chat" registration
        // above carries the resilience pipeline; BindBaseAddress resolves
        // Services:Chat[:BaseUrl] so the typed client inherits the same address.
        //
        // IChatTopologyMap is a singleton: it caches userId->memberId and
        // sortedPairKey->(channelId, sessions) so a conversation resolves to the
        // same generic channel/sessions across requests (the generic API has no
        // lookup-by-external-id).
        //
        // Impl is chosen by config presence, mirroring the wallet pattern:
        //   - Redis:ConnectionString set (appsettings.Production.json =
        //     192.168.2.50:6379) -> RedisChatTopologyMap. The in-memory map was
        //     lost on restart and not multi-replica safe (two replicas would split
        //     a conversation across two generic channels); Redis makes it durable
        //     + shared across replicas.
        //   - absent (dev/test) -> InMemoryChatTopologyMap, so the suite and local
        //     runs need no Redis.
        var redisConnectionString = config["Redis:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(redisConnectionString))
        {
            // PR #45 — the multiplexer MUST NOT block or throw at host boot.
            //
            // Previously this was `ConnectionMultiplexer.Connect(connStr)` with
            // StackExchange.Redis' default AbortOnConnectFail=true. When the
            // configured Redis (appsettings.Production.json = 192.168.2.50:6379)
            // is unreachable — e.g. in CI's network namespace, or during a Redis
            // outage in prod — Connect() blocks for the full connect timeout and
            // then throws. Because a hosted service (DataExportProcessor) resolves
            // a chat client during Host.StartAsync, that eager Connect ran on the
            // startup path: Kestrel never reached "Now listening", the container
            // smoke test's `curl /health/live` after 5s failed, and in prod a
            // transient Redis blip would crash-loop the whole gateway (reviewer
            // P2: a Redis outage should degrade chat id-mapping, not hard-fail the
            // BFF).
            //
            // Fix: parse ConfigurationOptions from the connection string and force
            //   * AbortOnConnectFail=false — Connect() returns immediately even
            //     when Redis is down; the multiplexer retries in the background and
            //     recovers without a restart.
            //   * ConnectTimeout=2000ms, ConnectRetry=1 — bounded, fast boot.
            // and wrap the multiplexer in a Lazy<> so the (now non-blocking)
            // Connect is deferred to first actual use, never to DI resolution.
            // RedisChatTopologyMap reads IConnectionMultiplexer.GetDatabase() per
            // op, so an as-yet-unconnected multiplexer degrades gracefully (ops
            // throw RedisConnectionException per-call and recover) instead of
            // taking the host down.
            var redisOptions = ConfigurationOptions.Parse(redisConnectionString!);
            redisOptions.AbortOnConnectFail = false;
            redisOptions.ConnectTimeout = 2000;
            redisOptions.ConnectRetry = 1;

            var lazyMultiplexer = new Lazy<IConnectionMultiplexer>(
                () => ConnectionMultiplexer.Connect(redisOptions),
                LazyThreadSafetyMode.ExecutionAndPublication);

            services.AddSingleton<IConnectionMultiplexer>(_ => lazyMultiplexer.Value);
            services.AddSingleton<IChatTopologyMap, RedisChatTopologyMap>();
        }
        else
        {
            services.AddSingleton<IChatTopologyMap, InMemoryChatTopologyMap>();
        }
        AttachStandardPipeline(
            services.AddHttpClient<IChatServiceClient, ChatServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Chat")));

        // T-migrate-gateway-proxies — typed client over the real notification-service
        // (FastAPI, Mongo jeeb_notifications). Hand-coded against verified routes on
        // notification-service/main.py (GET /notifications) pending an NSwag spec.
        // The named "notification" registration above carries the resilience pipeline;
        // BindBaseAddress resolves Services:Notification[:BaseUrl] so the typed client
        // inherits the same upstream address. Gated by FeatureFlags:UseUpstream:Notification.
        AttachStandardPipeline(
            services.AddHttpClient<INotificationServiceClient, NotificationServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Notification")));

        // T-backend-020 (JEEB-38): typed client over score-taking-service.
        // Carries its own bearer/ServiceAuth/resilience chain via
        // AttachStandardPipeline (BFF aggregation pattern).
        //
        // NOTE: score-taking-service is STALE (no appsettings entry in any
        // environment; not in the deployed fleet). The real ratings upstream is
        // feedback-service (block below). This typed registration is retained
        // only so the typed-client pipeline test keeps a registration to assert
        // against; IRatingService no longer routes its record-of-truth here.
        AttachStandardPipeline(
            services.AddHttpClient<IScoreServiceClient, ScoreServiceClient>(http =>
                BindBaseAddress(http, config, "Services:ScoreTaking")));

        // thin-BFF wire — feedback-service (host port 10064, liveness-only; NO
        // /health readiness route). Self-contained block: named client (resilience
        // pipeline) + typed IFeedbackServiceClient (own bearer/ServiceAuth/resilience
        // chain via AttachStandardPipeline). feedback-service is the REAL canonical
        // ratings upstream (Services:Feedback:BaseUrl = http://192.168.2.50:10064 in
        // appsettings.Production.json); IRatingService routes its rating
        // record-of-truth here when FeatureFlags:UseUpstream:Feedback is on, and
        // keeps the in-memory IRatingStore as the off/fallback path. Hand-coded
        // against feedback-service's Swashbuckle spec (/swagger/v1/swagger.json),
        // following the NotificationServiceClient precedent. See
        // IFeedbackServiceClient for the full score-taking-vs-feedback resolution.
        AddNamedDownstreamClient(services, config, "feedback", "Services:Feedback:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<IFeedbackServiceClient, FeedbackServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Feedback")));

        // thin-BFF wire — cdn-service (asset/object store for signed-ToS PDFs,
        // earnings statements, KYC/dispute evidence; 90-day retention + signed
        // URLs). Serves JEB-527 / JEB-519 / JEB-59. Self-contained block: named
        // client (resilience pipeline) + typed ICDNServiceClient (own
        // bearer/ServiceAuth/resilience chain via AttachStandardPipeline).
        //
        // NOT YET DEPLOYED: Services:Cdn:BaseUrl in appsettings.Production.json is
        // a PLACEHOLDER (http://192.168.2.50:PORT_TBD/) pending deployment, and
        // FeatureFlags:UseUpstream:Cdn is DEFAULT-OFF everywhere, so the gateway
        // never dials the unroutable host. Configuration is lazy
        // (AddNamedDownstreamClient does not throw on a missing/placeholder
        // BaseUrl; CdnController short-circuits to 503 while the flag is off), so
        // this registration is safe to ship before the service exists. Hand-coded
        // (cdn-service exposes no reachable OpenAPI doc yet), following the
        // OfferServiceClient / BanServiceClient precedent. See ICDNServiceClient
        // for the full deployment runbook (set BaseUrl, add readiness probe, flip
        // the flag).
        AddNamedDownstreamClient(services, config, "cdn", "Services:Cdn:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<ICDNServiceClient, CDNServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Cdn")));

        // T-BE-019 (JEB-55): typed client over one-time-password service for the
        // delivery-HANDOVER OTP (ApplicationId delivery_handover_{deliveryId}).
        // This is a POST-AUTH call inside the authenticated delivery flow, so it
        // carries the same bearer/ServiceAuth/resilience chain as the other
        // downstream clients (distinct from the PRE-AUTH sign-in OTP client in
        // Auth/OtpSignIn, which must not). The NSwag-generated client takes
        // (string baseUrl, HttpClient http); we resolve baseUrl via factory and
        // let HttpClient be the pipeline-configured instance.
        AttachStandardPipeline(
            services.AddHttpClient<IServiceOTPClient, ServiceOTPClient>((sp, http) =>
            {
                BindBaseAddress(http, config, "Services:ServiceOTP");
            })
            .AddTypedClient<IServiceOTPClient>((http, sp) =>
            {
                var baseUrl = config["Services:ServiceOTP:BaseUrl"]
                    ?? config["Services:ServiceOTP"]
                    ?? "http://localhost:5005";
                return new ServiceOTPClient(baseUrl, http);
            }));

        // T-backend-022 (push DB wiring): typed client over the
        // push-notification FastAPI service. The device-register write path
        // (PUT /api/v1/register) upserts into the push_notification Postgres
        // table — the "any call that writes to the push DB". PushController
        // consumes this when FeatureFlags:UseUpstream:Push is set, replacing
        // InMemoryDeviceTokenStore for that path. BindBaseAddress applies the
        // configured Services:PushNotification host with a trailing slash;
        // AttachStandardPipeline gives this typed client its own bearer +
        // X-Service-Auth + resilience chain (BFF aggregation pattern).
        AttachStandardPipeline(
            services.AddHttpClient<IPushNotificationClient, PushNotificationClient>(http =>
                BindBaseAddress(http, config, "Services:PushNotification")));

        // remote-user-preferences: the salehly-mirror scoped client
        // (ServiceRemoteUserPreferencesClient) is registered in Program.cs against
        // the "remote-user-preferences" named HttpClient above. No typed
        // IUserPreferencesClient registration here — the exact-salehly migration
        // removed that BFF seam; UserPreferencesController consumes the generated
        // client directly.

        // thin-BFF offer-service wire (FeatureFlags:UseUpstream:Offer). Typed
        // client over the real offer-service (Elixir/Phoenix, host port 10063,
        // liveness /health). Hand-coded — offer-service exposes NO OpenAPI doc
        // (/swagger/v1/swagger.json and /openapi.json both 404), so there is no
        // NSwag client to generate; OfferServiceClient is verified against the
        // routes in offer-service/lib/offer_service_web/router.ex. The named
        // resilience pipeline is attached the same way as every other typed
        // client; BindBaseAddress resolves Services:Offer[:BaseUrl] with a
        // trailing slash so relative paths like "api/v1/requests/{id}/offers"
        // resolve under the host. NOTE: offer-service authorizes on a
        // gateway-injected x-user-id header (its AuthenticatedUser plug), which
        // OfferServiceClient sets per call from the acting user id; the bearer-
        // forwarding handler is harmless here (offer-service ignores the bearer).
        AttachStandardPipeline(
            services.AddHttpClient<IOfferServiceClient, OfferServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Offer")));

        // thin-BFF wire (T-thin-bff-ban) — typed client over the real ban-service
        // (Rust / Actix-Web, Redis-backed, host port 10065, health /health). The
        // gateway's IJeeberRestrictionStore (the Jeeber abuse-control restriction
        // record-of-truth, consumed by CancellationService from BOTH
        // AdminCancellationsController and DeliveriesController) is swapped to a
        // ban-service-backed implementation in Program.cs when
        // FeatureFlags:UseUpstream:Ban is true; this typed client is its transport.
        // Hand-coded (BanServiceClient) over snake_case + OpenAPI-3.1-nullable wire,
        // mirroring the NotificationServiceClient precedent. BindBaseAddress
        // resolves Services:Ban[:BaseUrl] with a trailing slash so api/v1/ban/...
        // paths resolve under the host; AttachStandardPipeline gives this typed
        // client its own bearer + X-Service-Auth + resilience chain.
        AttachStandardPipeline(
            services.AddHttpClient<IBanServiceClient, BanServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Ban")));

        // thin-BFF fan-out (3 of 4): typed client over voice-transcription-service
        // (FastAPI, host port 10062, health /healthz, ready /readyz). NET-NEW thin
        // client — the upstream's route POST /v1/transcribe differs materially from
        // the OpenAI route the in-process WhisperClient calls, so this is NOT a
        // repoint of WhisperClient. TranscriptionController consumes this when
        // FeatureFlags:UseUpstream:Voice is true and falls back to the in-process
        // resilient Whisper path (ITranscriptionService) when false. BindBaseAddress
        // resolves Services:VoiceTranscription[:BaseUrl] with a trailing slash so the
        // relative "v1/transcribe" resolves under the host; AttachStandardPipeline
        // gives this typed client its own bearer + X-Service-Auth + resilience chain
        // (BFF aggregation pattern).
        AttachStandardPipeline(
            services.AddHttpClient<IVoiceTranscriptionClient, VoiceTranscriptionClient>(http =>
                BindBaseAddress(http, config, "Services:VoiceTranscription")));

        // realtime-comunication-service wire (FeatureFlags:UseUpstream:Realtime).
        // Typed client over the shared Elixir/Phoenix "LiveComm" service
        // (olivium-dev/realtime-comunication-service). The gateway uses its HTTP
        // ingest seam (POST /api/ingest/{topic}/{stream}, verified against
        // realtime-comunication-service/lib/live_comm_web/router.ex +
        // controllers/ingest_controller.ex) for SERVER-SIDE per-recipient chat
        // fan-out; mobile clients connect the Phoenix WebSocket channel
        // (topic:jeeb:chat, membership-validated join) directly, so the gateway
        // does not proxy the WebSocket. Serves JEB-1453/1449/1432/626/444/50/51/52.
        //
        // NOT-YET-DEPLOYED: realtime-comunication-service is in the olivium fleet
        // but NOT on the Jeeb swarm. Services:Realtime:BaseUrl is a marked
        // PLACEHOLDER (http://192.168.2.50:PORT_TBD/) in appsettings.Production.json
        // and FeatureFlags:UseUpstream:Realtime is OFF everywhere — RealtimeController
        // returns 503 ProblemDetails until the service is deployed and the
        // placeholder is replaced with the real host:port. The client is still
        // fully wired (named resilience pipeline + own bearer + X-Service-Auth +
        // resilience chain) so flipping the flag is the only remaining step.
        // Hand-coded (no NSwag): the upstream exposes no OpenAPI doc; the ingest
        // route is the single seam the gateway consumes. BindBaseAddress resolves
        // Services:Realtime[:BaseUrl] with a trailing slash so the relative
        // "api/ingest/{topic}/{stream}" resolves under the host.
        AddNamedDownstreamClient(services, config, "realtime", "Services:Realtime:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<IRealtimeCommunicationClient, RealtimeCommunicationClient>(http =>
                BindBaseAddress(http, config, "Services:Realtime")));

        // thin-BFF wire — contract-signing-service (FastAPI, api_prefix /v1;
        // immutable contract templates + per-party signatures; olivium-shared).
        // Serves the versioned Jeeb Terms-of-Service template jeeb_tos_v1
        // (JEB-40/JEB-41) via POST /v1/templates (RegisterTemplateAsync) and the
        // ToS-acceptance signature via POST /v1/contracts/{id}/signatures
        // (SignAsync). Self-contained block mirroring the feedback wire above:
        // named client (resilience pipeline) + typed IContractSigningServiceClient
        // (own bearer/ServiceAuth/resilience chain via AttachStandardPipeline).
        // NOT yet deployed to the Jeeb swarm — Services:ContractSigning:BaseUrl is a
        // PLACEHOLDER (http://192.168.2.50:PORT_TBD/) in appsettings.Production.json
        // and FeatureFlags:UseUpstream:ContractSigning defaults OFF everywhere, so
        // the controller 503s until the service is live. Hand-coded (no NSwag
        // artifact — the upstream's /openapi.json is unreachable from the build host
        // because the service is not yet deployed) against
        // contract-signing-service/app/routers/*.py, following the
        // NotificationServiceClient / OfferServiceClient precedent. BindBaseAddress
        // resolves Services:ContractSigning[:BaseUrl] with a trailing slash so the
        // relative "v1/templates" / "v1/contracts/..." resolve under the host.
        AddNamedDownstreamClient(services, config, "contract-signing", "Services:ContractSigning:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<IContractSigningServiceClient, ContractSigningServiceClient>(http =>
                BindBaseAddress(http, config, "Services:ContractSigning")));

        // thin-BFF wire — form-builder-service (FastAPI dynamic-forms upstream;
        // atlas pattern #15 "Dynamic forms"). Serves the versioned Jeeb KYC form
        // schema jeeb_jeeber_v1 (JEB-40/JEB-41) via GET /templates/{name}/schema.
        // Self-contained block mirroring the feedback wire above: named client
        // (resilience pipeline) + typed IFormBuilderServiceClient (own bearer/
        // ServiceAuth/resilience chain via AttachStandardPipeline). Deployed on the
        // Jeeb swarm — Services:FormBuilder:BaseUrl is http://192.168.2.50:10070/
        // (appsettings.Production.json). FeatureFlags:UseUpstream:FormBuilder still
        // defaults OFF, so flip it to true to route through the real upstream.
        // Hand-coded (no NSwag artifact) against form-builder-service/app/main.py,
        // following the NotificationServiceClient / OfferServiceClient precedent.
        // The FastAPI app exposes no /health route (only /docs + /openapi.json), so
        // the readiness probe is pinned to GET /openapi.json and registered Degraded
        // (see HealthCheckExtensions) — never 503s /health/ready.
        AddNamedDownstreamClient(services, config, "form-builder", "Services:FormBuilder:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<IFormBuilderServiceClient, FormBuilderServiceClient>(http =>
                BindBaseAddress(http, config, "Services:FormBuilder")));

        return services;
    }

    /// <summary>
    /// Accepts either a bare URL at <c>{section}</c> (e.g. "Services:Auth")
    /// or a nested <c>{section}:BaseUrl</c> key. The bare URL form is the
    /// shape used by the PR-A appsettings additions; the nested form is the
    /// pre-existing one used by named clients above. Both produce the same
    /// trailing-slash-corrected BaseAddress.
    /// </summary>
    private static void BindBaseAddress(HttpClient http, IConfiguration config, string section)
    {
        var direct = config[section];
        var nested = config[$"{section}:BaseUrl"];
        var baseUrl = !string.IsNullOrWhiteSpace(direct) ? direct : nested;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            // Trailing slash is required so relative paths like "api/jeeb/..."
            // resolve under the configured prefix rather than replacing it.
            http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        }
        http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Registers a single named downstream client with config-driven BaseAddress,
    /// a 10-second default timeout, and the org-standard resilience pipeline
    /// described in <see cref="ConfigureStandardResilience"/>.
    /// Configuration is lazy: a missing BaseUrl does not throw at startup; it
    /// throws on first use so dev environments can omit URLs for services they
    /// are not exercising.
    /// </summary>
    private static IHttpClientBuilder AddNamedDownstreamClient(
        IServiceCollection services,
        IConfiguration config,
        string name,
        string baseUrlConfigKey)
    {
        var builder = services.AddHttpClient(name, http =>
        {
            var baseUrl = config[baseUrlConfigKey];
            if (!string.IsNullOrWhiteSpace(baseUrl))
            {
                http.BaseAddress = new Uri(baseUrl);
            }

            // Per-request timeout below the resilience pipeline's overall budget.
            // The resilience pipeline owns retry timing; HttpClient.Timeout is the
            // hard upper bound for a single dispatched request.
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        // JEB-67 / T-BE-031 AC3 — every named downstream call carries the
        // inbound mobile JWT bearer + an HMAC-signed X-Service-Auth header.
        // Order matters: the auth headers must be added BEFORE the resilience
        // pipeline so retried attempts also carry them.
        builder.AddHttpMessageHandler<BearerForwardingHandler>();
        builder.AddHttpMessageHandler<ServiceAuthSigningHandler>();

        builder.AddResilienceHandler("standard", ConfigureStandardResilience);

        return builder;
    }

    /// <summary>
    /// Attaches the org-standard outbound pipeline to a TYPED client builder:
    /// <see cref="BearerForwardingHandler"/> (forwards the inbound mobile JWT) →
    /// <see cref="ServiceAuthSigningHandler"/> (attaches the HMAC X-Service-Auth
    /// header) → the standard resilience handler (retry / circuit-breaker /
    /// timeout). This mirrors <see cref="AddNamedDownstreamClient"/> so a
    /// <c>AddHttpClient&lt;IFoo, Foo&gt;</c> registration is production-safe on
    /// its own and does not silently bypass auth/resilience just because a
    /// like-named named client exists.
    ///
    /// Handler order is load-bearing: the auth headers are added BEFORE the
    /// resilience handler so every retried attempt still carries them.
    ///
    /// Do NOT call this for pre-auth clients (e.g. the OTP sign-in clients in
    /// <c>Auth/OtpSignIn</c>): they run before any caller token exists and must
    /// not emit bearer/X-Service-Auth headers.
    /// </summary>
    private static IHttpClientBuilder AttachStandardPipeline(IHttpClientBuilder builder)
    {
        builder.AddHttpMessageHandler<BearerForwardingHandler>();
        builder.AddHttpMessageHandler<ServiceAuthSigningHandler>();
        builder.AddResilienceHandler("standard", ConfigureStandardResilience);
        return builder;
    }

    /// <summary>
    /// The org-standard outbound resilience pipeline. Used by every downstream
    /// HTTP call from this gateway.
    ///
    /// - Retry: 3 attempts, exponential backoff (200ms base), jitter, only on
    ///   transient HTTP errors and HTTP 5xx / 408 / 429.
    /// - Circuit breaker: trips at 50% failure ratio over a 30-second sliding
    ///   window (minimum 10 throughput), breaks for 30 seconds.
    /// - Timeout: 10 seconds per attempt — keeps a single slow downstream from
    ///   pinning a request thread.
    /// </summary>
    private static void ConfigureStandardResilience(ResiliencePipelineBuilder<HttpResponseMessage> b)
    {
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
        });

        b.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            SamplingDuration = TimeSpan.FromSeconds(30),
            FailureRatio = 0.5,
            MinimumThroughput = 10,
            BreakDuration = TimeSpan.FromSeconds(30),
        });

        b.AddTimeout(new HttpTimeoutStrategyOptions
        {
            Timeout = TimeSpan.FromSeconds(10),
        });
    }
}
