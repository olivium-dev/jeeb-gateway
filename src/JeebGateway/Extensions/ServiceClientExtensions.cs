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
/// <see cref="Services/Generated"/>). When you add a typed client, register it
/// with <c>services.AddHttpClient&lt;IFooClient, FooClient&gt;("foo")</c> AFTER
/// calling <see cref="AddDownstreamClients"/> so the typed registration inherits
/// the named client's BaseAddress and resilience pipeline.
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
        // TODO(T-backend-bff-auth): auth-service — wire NSwag-generated AuthServiceClient
        //   contract: src/JeebGateway/contracts/auth-service.openapi.json
        //   migrates: AuthController, TokensController (currently in-memory)
        AddNamedDownstreamClient(services, config, "auth", "Services:Auth:BaseUrl");

        // TODO(T-backend-bff-chat): chat-service — wire NSwag-generated ChatServiceClient
        //   contract: src/JeebGateway/contracts/chat-service.openapi.json
        //   migrates: ChatController (currently SignalR + InMemoryChatMessageStore)
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

        // T-migrate-gateway-proxies (PR-A): typed clients on top of the named
        // HttpClient registrations above. Hand-coded against verified upstream
        // routes pending NSwag-generated artifacts. Each controller checks the
        // matching FeatureFlags:UseUpstream:* flag and falls back to the
        // legacy in-memory implementation when false.
        services.AddHttpClient<IAuthServiceClient, AuthServiceClient>(http =>
            BindBaseAddress(http, config, "Services:Auth"));
        services.AddHttpClient<IDeliveryServiceClient, DeliveryServiceClient>(http =>
            BindBaseAddress(http, config, "Services:Delivery"));
        services.AddHttpClient<IMatchingServiceClient, MatchingServiceClient>(http =>
            BindBaseAddress(http, config, "Services:Matching"));
        services.AddHttpClient<IGeolocationServiceClient, GeolocationServiceClient>(http =>
            BindBaseAddress(http, config, "Services:Geolocation"));

        // T-backend-020 (JEEB-38): typed client over score-taking-service.
        // The named "score-taking" registration above carries BaseAddress +
        // the standard resilience pipeline; this typed registration hangs
        // off it via the BFF aggregation pattern.
        services.AddHttpClient<IScoreServiceClient, ScoreServiceClient>(http =>
            BindBaseAddress(http, config, "Services:ScoreTaking"));

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
