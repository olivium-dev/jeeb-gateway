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

        // TODO(T-backend-bff-wallet): wallet-service — wire NSwag-generated WalletServiceClient
        //   contract: src/JeebGateway/contracts/wallet-service.openapi.json
        //   migrates: (no existing controller — net-new wallet endpoints will consume it)
        AddNamedDownstreamClient(services, config, "wallet", "Services:Wallet:BaseUrl");

        // TODO(T-backend-bff-matching): matching (FastAPI) — wire NSwag-generated MatchingServiceClient
        //   contract: src/JeebGateway/contracts/matching.openapi.json
        //   migrates: MatchingController (currently MatchingService + InMemory* providers)
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
            services.AddSingleton<IConnectionMultiplexer>(_ =>
                ConnectionMultiplexer.Connect(redisConnectionString!));
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
        AttachStandardPipeline(
            services.AddHttpClient<IScoreServiceClient, ScoreServiceClient>(http =>
                BindBaseAddress(http, config, "Services:ScoreTaking")));

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
    /// Public entry point that applies the same standard pipeline as the typed
    /// downstream clients to the wallet <see cref="IHttpClientBuilder"/>
    /// registered in <c>Program.cs</c>. The wallet client is wired there (not in
    /// <see cref="AddDownstreamClients"/>) because it composes with settlement
    /// services declared in the same block; this keeps it on the identical
    /// bearer + X-Service-Auth + resilience chain. The bearer/ServiceAuth
    /// handlers are registered transiently by <see cref="AddDownstreamClients"/>,
    /// which Program.cs calls first.
    /// </summary>
    public static IHttpClientBuilder AttachWalletPipeline(IHttpClientBuilder builder)
        => AttachStandardPipeline(builder);

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
