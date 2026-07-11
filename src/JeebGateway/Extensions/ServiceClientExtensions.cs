using JeebGateway.Financials;
using JeebGateway.Services.Bff;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Http.Resilience;
using Polly;

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

        // S06 / ADR-HB-001 AUTH CONTRACT — the heart-beat-only static
        // X-Service-Auth-Key handler (attached to the heart-beat typed client
        // below). heart-beat accepts a static service-auth key OR a
        // JWKS-validated user JWT, but NOT the gateway's HMAC X-Service-Auth
        // scheme, so the gateway authenticates as the trusted caller process via
        // this static key. No-op when the key is unconfigured (flag-off default).
        services.AddTransient<Services.Clients.HeartBeatServiceAuthKeyHandler>();

        // TODO(T-backend-bff-auth): auth-service — wire NSwag-generated AuthServiceClient
        //   contract: src/JeebGateway/contracts/auth-service.openapi.json
        //   migrates: AuthController, TokensController (currently in-memory)
        AddNamedDownstreamClient(services, config, "auth", "Services:Auth:BaseUrl");

        // chat-service — registered separately as the salehly-style named client
        //   "ServiceChatClient" (ChatServiceApi:BaseUrl) in Program.cs, consumed by
        //   the NSwag ServiceChatClient that ChatController passes through. It is
        //   NOT part of this named-downstream-client set (no bearer/ServiceAuth
        //   pipeline), matching salehly-gateway exactly.

        // TODO(T-backend-bff-user): user-management — wire NSwag-generated UserManagementClient
        //   contract: src/JeebGateway/contracts/user-management.openapi.json
        //   migrates: UsersController, AdminUsersController (currently InMemoryUsersStore)
        // CONFIG-KEY ALIGNMENT: the live user-management seam is the scoped
        // ServiceUserManagementClient registered in Program.cs against the
        // top-level UserManagementServiceApi:BaseUrl key (and the readiness probe
        // in HealthCheckExtensions uses the same key). This placeholder named
        // client must read the SAME canonical key so it is correct the moment a
        // future NSwag UserManagementClient is wired onto it — not a phantom
        // Services:UserManagement:BaseUrl that nothing else reads.
        AddNamedDownstreamClient(services, config, "user-management", "UserManagementServiceApi:BaseUrl");

        // wallet-service is wired in Program.cs as a salehly-mirrored named
        // IHttpClientFactory client ("ServiceWalletClient" bound to
        // WalletServiceApi:BaseUrl) + scoped ServiceWalletClient typed client,
        // not via this generic named-downstream helper.

        // matching (FastAPI) — DB-backed read of a user's match preferences
        //   (GET /api/v1/matches/{user_id}), consumed by MatchingController's
        //   GetMatchingUsers. Courier matching /run was relocated to
        //   delivery-service; see IDeliveryServiceClient.RunMatchingAsync.
        AddNamedDownstreamClient(services, config, "matching", "Services:Matching:BaseUrl");

        // notification-service — registered separately as the salehly-style named
        //   client "ServiceNotificationClient" (ServiceNotificationClient:BaseUrl)
        //   in Program.cs, consumed by the NSwag ServiceNotificationClient that
        //   NotificationController passes through. It is NOT part of this
        //   named-downstream-client set (no bearer/ServiceAuth pipeline), matching
        //   salehly-gateway exactly.

        // TODO(T-backend-bff-geo): geolocation-service (FastAPI) — wire GeolocationServiceClient
        //   contract: src/JeebGateway/contracts/geolocation-service.openapi.json
        //   migrates: LocationController, AdminZonesController (currently InMemoryLocationStore + InMemoryGeoIndex)
        AddNamedDownstreamClient(services, config, "geolocation", "Services:Geolocation:BaseUrl");

        // push-notification — registered separately as the salehly-style named
        //   client "ServicePushNotificationClient" (PushNotificationServiceApi:BaseUrl)
        //   in Program.cs, consumed by the NSwag ServicePushNotificationClient that
        //   PushNotificationController passes through. It is NOT part of this
        //   named-downstream-client set (no bearer/ServiceAuth pipeline), matching
        //   salehly-gateway exactly.

        // TODO(T-backend-bff-delivery): delivery-service (Go) — wire DeliveryServiceClient
        //   contract: src/JeebGateway/contracts/delivery-service.openapi.json
        //   migrates: DeliveriesController, RequestsController, RequestOffersController,
        //             OffersController, CancellationController, OtpHandoverController
        //             (currently IRequestsStore + InMemoryRequestsStore)
        AddNamedDownstreamClient(services, config, "delivery", "Services:Delivery:BaseUrl");

        // score-taking-service — DELETED (owner directive: remove completely, never use).
        // Jeeb ratings are owned by the in-gateway mutual-blind state machine with the
        // optional feedback-service store swap (FeatureFlags:UseUpstream:Ratings). The
        // named + typed score-taking client registrations and their config keys are gone.

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

        // geolocation typed client — the SINGLE registration is the NSwag-generated
        // Services.Generated.GeolocationService.IGeolocationServiceClient below.
        // The legacy hand-coded Services.Clients.IGeolocationServiceClient /
        // GeolocationServiceClient was removed (dead code — never injected anywhere):
        // registering BOTH crashed boot because IHttpClientFactory keys typed clients
        // by the SHORT type name (both were "IGeolocationServiceClient"), throwing an
        // InvalidOperationException unconditionally at startup (process exit 139).

        // chat-service — REMOVED from this set. The jeeb 1:1 conversation BFF
        // (ChatServiceClient) + Redis topology map (RedisChatTopologyMap /
        // InMemoryChatTopologyMap / IChatTopologyMap) + IConnectionMultiplexer
        // singleton were removed with the salehly mirror. Chat is now a stateless
        // passthrough: the NSwag ServiceChatClient is registered in Program.cs as
        // the named client "ServiceChatClient" (ChatServiceApi:BaseUrl) and consumed
        // directly by ChatController, exactly as salehly-gateway wires it.

        // notification-service — REMOVED from this set. The jeeb-specific
        // notification read BFF (INotificationServiceClient / NotificationServiceClient)
        // and its NotificationsController (/users/me/notifications) were removed with
        // the salehly mirror. Notification is now a stateless passthrough: the NSwag
        // ServiceNotificationClient is registered in Program.cs as the named client
        // "ServiceNotificationClient" (ServiceNotificationClient:BaseUrl) and consumed
        // directly by NotificationController, exactly as salehly-gateway wires it.

        // score-taking-service typed client — DELETED (owner directive). See note above.

        // Gap 3 (JEB compliment BFF): NSwag-generated typed client over the shared
        // compliment-service, with its own bearer/ServiceAuth/resilience chain via
        // AttachStandardPipeline. Gated by FeatureFlags:UseUpstream:Compliment (default
        // OFF → ComplimentsController returns 503). BaseUrl is optional / no-fail-closed:
        // BindBaseAddress leaves BaseAddress null for an unset/placeholder host so the
        // client constructs even while the kill switch is off.
        // The NSwag-generated ComplimentServiceClient ctor is (string baseUrl, HttpClient http),
        // so — exactly like the ServiceOTP registration below — the baseUrl must be supplied via
        // AddTypedClient; a bare AddHttpClient<I,Impl> would have ActivatorUtilities try (and fail)
        // to resolve `string baseUrl` from DI at controller-activation time. BindBaseAddress still
        // sets HttpClient.BaseAddress for the resilience pipeline; the ctor baseUrl mirrors it
        // (empty/placeholder when the kill switch is off, which is safe — the controller 503s first).
        AttachStandardPipeline(
            services.AddHttpClient<
                JeebGateway.Services.Generated.ComplimentService.IComplimentServiceClient,
                JeebGateway.Services.Generated.ComplimentService.ComplimentServiceClient>(http =>
                BindBaseAddress(http, config, "ComplimentServiceApi"))
            .AddTypedClient<JeebGateway.Services.Generated.ComplimentService.IComplimentServiceClient>((http, sp) =>
            {
                var baseUrl = config["ComplimentServiceApi:BaseUrl"]
                    ?? config["ComplimentServiceApi"]
                    ?? string.Empty;
                return new JeebGateway.Services.Generated.ComplimentService.ComplimentServiceClient(baseUrl, http);
            }));

        // Gap 1 (geolocation): NSwag-shaped typed client over the shared
        // geolocation-service, consumed by GeoServiceLocationStore when
        // FeatureFlags:UseUpstream:Geolocation is ON (flag-gated store swap in Program.cs).
        // This is the ONLY IGeolocationServiceClient registration (the legacy
        // Services.Clients one was removed — see note above); it is the live client
        // the store binds to and the one the typed-client pipeline assertion covers.
        AttachStandardPipeline(
            services.AddHttpClient<
                JeebGateway.Services.Generated.GeolocationService.IGeolocationServiceClient,
                JeebGateway.Services.Generated.GeolocationService.GeolocationServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Geolocation")));

        // feedback-service: the gateway now mirrors salehly-gateway's
        // ServiceFeedbackClient + FeedbackController exactly (named + scoped NSwag
        // client bound to FeedbackServiceApi:BaseUrl, registered in Program.cs).
        // The former jeeb-specific hand-coded IFeedbackServiceClient /
        // FeedbackServiceClient (a 3-method submit+read seam over a resilience
        // pipeline, bound to the nested Services:Feedback key) was removed with
        // the literal salehly replace, so there is no typed-client registration
        // here anymore.

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

        // JEBV4-259 (approach B) — a SEPARATE named client for the KYC-photo
        // streaming PUT proxy (CdnUploadProxyController). Deliberately carries
        // NEITHER the resilience/retry handler (a retried request cannot rewind the
        // client's upload body stream — retrying a stream mid-upload is always
        // wrong) NOR the bearer / X-Service-Auth handlers (the signed-PUT URL is
        // bearer-free; the HMAC sig in the query is the authorization cdn
        // validates). Just the cdn BaseAddress + a generous timeout for a photo
        // upload over a slow mobile link. Lazy/safe base binding (BindBaseAddress
        // leaves BaseAddress null for a placeholder host; the proxy 502s rather
        // than dialing an unroutable address).
        services.AddHttpClient(JeebGateway.Services.Cdn.CdnUploadUrlResolver.ProxyHttpClientName, http =>
        {
            BindBaseAddress(http, config, "Services:Cdn");
            http.Timeout = TimeSpan.FromSeconds(100);
        })
        // CWE-918 (SSRF): pin the primary handler to NOT follow redirects. This proxy
        // dials a FIXED signed-PUT path on the internal cdn; if cdn ever returned a 3xx
        // the default AllowAutoRedirect=true would have the gateway chase the Location
        // to an arbitrary host. A redirect must be RELAYED to the client verbatim (the
        // proxy is a dumb pipe), never chased server-side. Scoped to THIS client only.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        });

        // thin-BFF wire — kyc-service (S03 / ADR-0004): the OWNING microservice for
        // the KYC domain (SM-6 state machine, submission aggregate, ToS-acceptance
        // record, Idempotency-Key dedup, role-grant DECISION). Per ARCH LAW
        // kyc-service calls no sibling; this is the gateway's single seam onto it.
        //
        // NOT YET DEPLOYED: the repo olivium-dev/kyc-service + Postgres jeeb_kyc are
        // owner/SSH ESCALATED. Services:Kyc:BaseUrl in appsettings.Production.json is
        // a PLACEHOLDER (http://192.168.2.50:PORT_TBD/) and
        // FeatureFlags:UseUpstream:Kyc is DEFAULT-OFF everywhere, so the gateway
        // never dials the unroutable host (the KycBffSeam serves the interim
        // in-gateway store while off). Lazy config (no throw on placeholder BaseUrl)
        // makes this safe to ship before the service exists. Hand-coded (kyc-service
        // exposes no reachable OpenAPI doc yet); regenerate via NSwag once it does.
        AddNamedDownstreamClient(services, config, "kyc", "Services:Kyc:BaseUrl");
        AttachStandardPipeline(
            services.AddHttpClient<IKycServiceClient, KycServiceClient>(http =>
                BindBaseAddress(http, config, "Services:Kyc")));

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

        // push-notification — REMOVED from this set. The jeeb-specific
        // device-register passthrough (IPushNotificationClient / PushNotificationClient)
        // and its PushController (POST /push/devices) were removed with the salehly
        // mirror. The device-register / notification surface is now the salehly-mirrored
        // PushNotificationController, backed by the NSwag ServicePushNotificationClient
        // registered in Program.cs as the named client "ServicePushNotificationClient"
        // (PushNotificationServiceApi:BaseUrl), consumed directly by the controller,
        // exactly as salehly-gateway wires it.

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

        // thin-BFF wire — heart-beat (S06 / ADR-HB-001): the NEW reusable presence
        // service (Go + Redis) that owns the online bit + lastSeenAt recency + the
        // TTL idle-sweep. Typed client over PATCH /v1/presence + GET
        // /v1/presence/{userId} (camelCase wire). Consumed by AvailabilityController
        // when FeatureFlags:Heartbeat:Enabled is true; while off (the default this
        // round) the availability surface keeps using the delivery-service presence
        // wire, so this registration is inert until the flag flips.
        //
        // NOT YET DEPLOYED: Services:HeartBeat:BaseUrl in appsettings.Production.json
        // is a PLACEHOLDER (http://192.168.2.50:PORT_TBD/) pending the one-time repo
        // create + Redis provisioning + GHA deploy of heart-beat, and
        // FeatureFlags:Heartbeat:Enabled is DEFAULT-OFF everywhere, so the gateway
        // never dials the unroutable host. Lazy config (BindBaseAddress does not
        // throw on a placeholder/PORT_TBD BaseUrl) makes this safe to ship before the
        // service exists. Hand-coded (heart-beat exposes no reachable OpenAPI doc
        // yet); regenerate via NSwag once it does. AttachStandardPipeline gives this
        // typed client its own bearer + X-Service-Auth + resilience chain.
        AddNamedDownstreamClient(services, config, "heart-beat", "Services:HeartBeat:BaseUrl");
        var heartBeatBuilder = services.AddHttpClient<IHeartBeatServiceClient, HeartBeatServiceClient>(http =>
            BindBaseAddress(http, config, "Services:HeartBeat"));
        // S06 AUTH CONTRACT — handler order is load-bearing (handlers added EARLIER
        // are OUTER; the LAST-added runs INNERMOST, closest to the wire):
        //   1. BearerForwardingHandler        — forward the inbound mobile JWT
        //   2. ServiceAuthSigningHandler      — (fleet) HMAC X-Service-Auth, if enabled
        //   3. HeartBeatServiceAuthKeyHandler — set the static X-Service-Auth-Key AND
        //      STRIP any inherited HMAC, so heart-beat's middleware (which checks the
        //      HMAC FIRST and 401s on a non-shared signature without falling through)
        //      always reaches the static-key path. This makes the fresh
        //      HEARTBEAT_SERVICE_AUTH_KEY the authoritative credential and is robust
        //      even if ServiceAuth:Enabled is later flipped on fleet-wide.
        //   4. resilience (retry/breaker/timeout) — OUTERMOST so the static key is
        //      re-applied on every retried attempt.
        // We inline (not AttachStandardPipeline) only to insert (3) between the auth
        // headers and the resilience handler.
        heartBeatBuilder.AddHttpMessageHandler<BearerForwardingHandler>();
        heartBeatBuilder.AddHttpMessageHandler<ServiceAuthSigningHandler>();
        heartBeatBuilder.AddHttpMessageHandler<Services.Clients.HeartBeatServiceAuthKeyHandler>();
        heartBeatBuilder.AddResilienceHandler("standard", ConfigureStandardResilience);

        // JEB-1484 (GR3 + GR4) — typed client over the Unified Payment Gateway's
        // GENERIC external-settlement endpoint (POST /api/v1/payments/settlements/
        // record). The Jeeb fee policy is computed in the gateway
        // (CommissionCalculator / SettlementService); UPG records the pre-computed
        // gross/fee/net. UpgSettlementLedgerClient (the ISettlementLedgerClient
        // impl, wired in Program.cs behind FeatureFlags:UseUpstream:Payments) maps
        // a settlement onto this client. AttachStandardPipeline gives it the same
        // bearer + X-Service-Auth (ServiceAuth gating) + resilience chain as every
        // other typed client. BindBaseAddress resolves Services:UnifiedPayment
        // [:BaseUrl] with a trailing slash so "api/v1/payments/..." resolves under
        // the host (lazy/safe: a missing BaseUrl leaves BaseAddress null and the
        // flag stays off). HAND-CODED transport pending NSwag regeneration of
        // ServiceUnifiedPaymentGatewayClient from
        // contracts/unified-payment-gateway.openapi.json (deferred to CI; the build
        // host has no dotnet nswag tool) — tracked as GR4 debt.
        AttachStandardPipeline(
            services.AddHttpClient<IUpgSettlementClient, UpgSettlementClient>(http =>
                BindBaseAddress(http, config, "Services:UnifiedPayment")));

        AddDbProbeClients(services, config);

        return services;
    }

    /// <summary>
    /// Registers the additive, read-only "DB probe" named HttpClients consumed
    /// by <see cref="JeebGateway.Controllers.GatewayDbProbeController"/>. These
    /// give the gateway (the product front door) a verifiable GET pass-through to
    /// one DB-backed read on every remaining upstream so each persistence layer
    /// can be exercised END-TO-END THROUGH THE GATEWAY with a minted token —
    /// without adding a typed-client method or DTO per route (which would drift
    /// from the snake_case FastAPI / Phoenix / Actix wire shapes).
    ///
    /// Each client carries the org-standard bearer + X-Service-Auth + Polly
    /// resilience chain (via <see cref="AddNamedDownstreamClient"/>) so the probe
    /// routes behave exactly like every other downstream call. BaseUrl binding is
    /// lazy: a missing key does not throw at startup; the controller surfaces a
    /// 503 ProblemDetails when an upstream is not configured in an environment.
    ///
    /// ADDITIVE ONLY — no existing client, route, or flag is changed here.
    /// </summary>
    private static void AddDbProbeClients(IServiceCollection services, IConfiguration config)
    {
        // notification-service (Mongo read) — GET /notifications. Reuse the same
        // upstream base the salehly-mirrored ServiceNotificationClient targets
        // (host port 10026) so the probe and the passthrough agree on the host.
        AddNamedDownstreamClient(services, config, "db-probe-notification", "ServiceNotificationClient:BaseUrl");

        // geolocation-service (PG read) — GET /locations/user/{user_id}.
        AddNamedDownstreamClient(services, config, "db-probe-geolocation", "Services:Geolocation:BaseUrl");

        // unified_payment_gateway (PG read, READ-ONLY) —
        // GET /api/v1/payments/cod_jeeb/by-delivery/{deliveryId}.
        AddNamedDownstreamClient(services, config, "db-probe-unified-payment", "Services:UnifiedPayment:BaseUrl");

        // realtime-comunication-service (PG read) — GET /admin/topics.
        AddNamedDownstreamClient(services, config, "db-probe-realtime", "Services:Realtime:BaseUrl");

        // compliment-service (PG read) — GET /list.
        AddNamedDownstreamClient(services, config, "db-probe-compliment", "Services:Compliment:BaseUrl");

        // ban-service (Redis read) — GET /api/v1/ban/{user_id}/status.
        AddNamedDownstreamClient(services, config, "db-probe-ban", "Services:Ban:BaseUrl");

        // one-time-password service (PG read) — GET /api/OTP/status/{phoneNumber}.
        AddNamedDownstreamClient(services, config, "db-probe-otp", "Services:ServiceOTP:BaseUrl");
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
            //
            // Lazy/safe config (matches the kill-switch contract): a PLACEHOLDER
            // BaseUrl for a not-yet-deployed upstream (e.g.
            // http://192.168.2.50:PORT_TBD/) is NOT a parseable absolute Uri, so
            // we must not throw at client-construction time — the consuming
            // controller short-circuits to 503 while the feature flag is off and
            // never dials the address. Leaving BaseAddress null is harmless until
            // a real BaseUrl is set and the flag is flipped.
            var normalised = baseUrl.TrimEnd('/') + "/";
            if (Uri.TryCreate(normalised, UriKind.Absolute, out var uri))
            {
                try
                {
                    // Touch Port: a placeholder like ":PORT_TBD" can pass TryCreate
                    // but throw on member access; force the parse here so we fall
                    // through to "leave BaseAddress null" instead of 500-ing later.
                    _ = uri.Port;
                    http.BaseAddress = uri;
                }
                catch (UriFormatException)
                {
                    // Placeholder BaseUrl for a not-yet-deployed upstream — skip.
                }
            }
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
    /// - Retry: 3 attempts, exponential backoff (200ms base), jitter, on
    ///   transient HTTP errors and HTTP 5xx / 408 — but NOT on HTTP 429.
    /// - Circuit breaker: trips at 50% failure ratio over a 30-second sliding
    ///   window (minimum 10 throughput), breaks for 30 seconds.
    /// - Timeout: 10 seconds per attempt — keeps a single slow downstream from
    ///   pinning a request thread.
    ///
    /// <para><b>Why 429 is excluded from retry (OTP-429, S02 N2 fix).</b> The
    /// default <see cref="HttpRetryStrategyOptions"/> predicate
    /// (<c>HttpClientResiliencePredicates.IsTransient</c>) treats HTTP 429
    /// (Too Many Requests) as a transient, retryable response AND honors the
    /// upstream <c>Retry-After</c> header (<c>ShouldRetryAfterHeader = true</c>).
    /// For the shared one-time-password verify-lockout, the upstream returns
    /// <b>429 + Retry-After: 60</b> on the 3rd wrong code; the default pipeline
    /// then SLEEPS ~60s per retry attempt before re-issuing the (still 429)
    /// request. Stacked across attempts that vastly exceeds any caller timeout,
    /// so the inbound request appears to HANG / drop the connection instead of
    /// returning a clean 429 <c>too_many_attempts</c> that
    /// <see cref="JeebGateway.Auth.OtpSignIn.AuthOtpController"/> already maps.
    /// A 429 is a deliberate, client-actionable throttle (back off as instructed),
    /// NOT a transient fault — retrying it inside a single inbound request is
    /// always wrong (it never succeeds and only adds latency). We therefore
    /// override <c>ShouldHandle</c> to retry transient errors + 5xx + 408 only,
    /// and forward any 429 immediately so the controller's existing
    /// <c>ApiException.StatusCode == 429</c> branch surfaces the proper
    /// ProblemDetails (with Retry-After) without delay. This is correct fleet-wide
    /// (delivery-handover OTP, every downstream): a 429 is forwarded, never spun on.
    /// </para>
    /// </summary>
    private static void ConfigureStandardResilience(ResiliencePipelineBuilder<HttpResponseMessage> b)
    {
        b.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            // Retry transient faults + 5xx + 408, but NEVER 429. Mirrors the
            // default IsTransient predicate MINUS TooManyRequests so a deliberate
            // upstream throttle is forwarded immediately (see remarks above).
            ShouldHandle = args => ShouldRetryStandard(args.Outcome.Exception, args.Outcome.Result)
                ? PredicateResult.True()
                : PredicateResult.False(),
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

    /// <summary>
    /// The standard-pipeline retry predicate, factored out for unit testing
    /// (OTP-429, S02 N2). Retries network/timeout faults and transient HTTP
    /// responses (5xx + 408) but NEVER HTTP 429 — a deliberate, client-actionable
    /// throttle must be forwarded immediately, not spun on (retrying a 429 +
    /// Retry-After is what turned the OTP verify-lockout into a connection hang).
    /// </summary>
    /// <param name="exception">The outcome exception, if the attempt threw.</param>
    /// <param name="response">The outcome response, if the attempt completed.</param>
    /// <returns><c>true</c> if the attempt should be retried; otherwise <c>false</c>.</returns>
    internal static bool ShouldRetryStandard(Exception? exception, HttpResponseMessage? response)
    {
        // Network/timeout faults (HttpRequestException, TimeoutRejectedException)
        // remain retryable exactly as the default IsTransient predicate treats them.
        if (exception is not null)
            return true;

        if (response is null)
            return false;

        // 429 is client-actionable, not transient — forward, do not retry.
        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            return false;

        // 5xx and 408 are transient and retryable.
        var code = (int)response.StatusCode;
        return code >= 500 || code == 408;
    }

    /// <summary>
    /// JEBV4-58 (PP-7) — attaches ONLY the org-standard resilience handler
    /// (retry / circuit-breaker / timeout, see <see cref="ConfigureStandardResilience"/>)
    /// to a NAMED client builder, deliberately WITHOUT the
    /// <see cref="BearerForwardingHandler"/> / <see cref="ServiceAuthSigningHandler"/>
    /// pair that <see cref="AttachStandardPipeline"/> adds for post-auth typed
    /// clients.
    ///
    /// The PP-7 stragglers (Chat/Notification/Feedback/Catalog named clients in
    /// Program.cs) are exact salehly-gateway mirrors that were deliberately
    /// registered with NO bearer/ServiceAuth handler chain (see the "NOT part of
    /// this named-downstream-client set" notes earlier in this file — the
    /// upstream services authorize on their own terms, not the gateway's
    /// caller JWT). Routing them through the full <see cref="AttachStandardPipeline"/>
    /// would silently start forwarding the caller's bearer + signing
    /// X-Service-Auth on every call — an auth-behavior change out of scope for
    /// a resilience-only fix. This helper closes the actual PP-7 gap (default
    /// 100s HttpClient.Timeout, no retry, no breaker) without touching
    /// auth-header behavior.
    ///
    /// Safe for GET-dominant / idempotent-write clients only — see
    /// <see cref="AttachBreakerAndTimeoutOnly"/> for clients that also carry a
    /// non-idempotent, non-idempotency-keyed POST (push dispatch, wallet
    /// money-mutation) where retrying a transient 5xx/timeout risks a
    /// duplicate side effect upstream.
    /// </summary>
    internal static IHttpClientBuilder AttachResilienceOnly(IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("standard", ConfigureStandardResilience);
        return builder;
    }

    /// <summary>
    /// JEBV4-58 (PP-7) — attaches circuit-breaker + timeout ONLY (no retry, no
    /// bearer/ServiceAuth) to a NAMED client builder. Use for a client that
    /// mixes safe reads with a non-idempotent POST that carries no
    /// idempotency key: <c>ServicePushNotificationClient</c> (device
    /// register/broadcast/send-to-user — a retried 5xx could duplicate-deliver
    /// a push) and <c>ServiceWalletClient</c> (money-adjacent; the same named
    /// client also backs <c>holder/add</c> and the deactivate endpoints, so a
    /// retried 5xx after the upstream already applied the mutation would risk
    /// double-crediting/deactivating a wallet). The breaker still trips on a
    /// truly failing upstream and the 10s per-attempt timeout still bounds a
    /// hung call; only the "retry the same request again" behavior is
    /// withheld until these calls carry an idempotency key.
    /// </summary>
    internal static IHttpClientBuilder AttachBreakerAndTimeoutOnly(IHttpClientBuilder builder)
    {
        builder.AddResilienceHandler("standard-no-retry", ConfigureBreakerAndTimeoutOnly);
        return builder;
    }

    /// <summary>
    /// Circuit-breaker + timeout halves of <see cref="ConfigureStandardResilience"/>,
    /// factored out for <see cref="AttachBreakerAndTimeoutOnly"/> — deliberately
    /// omits the retry strategy for non-idempotent, non-idempotency-keyed POSTs
    /// (see that method's remarks).
    /// </summary>
    private static void ConfigureBreakerAndTimeoutOnly(ResiliencePipelineBuilder<HttpResponseMessage> b)
    {
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
