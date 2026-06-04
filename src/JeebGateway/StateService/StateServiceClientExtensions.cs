using System.Net;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using Polly.Timeout;

namespace JeebGateway.StateService;

/// <summary>
/// Single registration point for the jeeb-state-service NSwag client and the
/// write-through store decorators that back the gateway's durable interfaces.
/// Keeps Program.cs clean (one call per area is wired from here).
/// </summary>
public static class StateServiceClientExtensions
{
    /// <summary>
    /// Registers <see cref="IJeebStateServiceClient"/> as an
    /// IHttpClientFactory-managed typed client with a standard Polly v8
    /// resilience pipeline (retry + circuit breaker + timeout). A
    /// state-service outage trips the breaker and the decorators degrade to
    /// the local fallback instead of cascading fleet-wide 500s.
    /// </summary>
    public static IServiceCollection AddJeebStateServiceClient(
        this IServiceCollection services,
        StateServiceOptions options)
    {
        var baseUrl = options.BaseUrl.TrimEnd('/') + "/";

        services
            .AddHttpClient<IJeebStateServiceClient, JeebStateServiceClient>(http =>
            {
                http.BaseAddress = new Uri(baseUrl);
                http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
            })
            .AddResilienceHandler("jeeb-state-service", pipeline =>
            {
                pipeline.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = 2,
                    Delay = TimeSpan.FromMilliseconds(150),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    // Only retry transient transport/5xx — never 4xx, which
                    // carry the domain outcome (409 reuse, 404 not-found, …).
                    ShouldHandle = args => args.Outcome switch
                    {
                        { Exception: HttpRequestException } => PredicateResult.True(),
                        { Exception: TimeoutRejectedException } => PredicateResult.True(),
                        { Result.StatusCode: HttpStatusCode.ServiceUnavailable } => PredicateResult.True(),
                        { Result.StatusCode: HttpStatusCode.BadGateway } => PredicateResult.True(),
                        { Result.StatusCode: HttpStatusCode.GatewayTimeout } => PredicateResult.True(),
                        _ => PredicateResult.False()
                    }
                });
                pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
                {
                    SamplingDuration = TimeSpan.FromSeconds(10),
                    FailureRatio = 0.5,
                    MinimumThroughput = 8,
                    BreakDuration = TimeSpan.FromSeconds(15)
                });
                pipeline.AddTimeout(TimeSpan.FromSeconds(options.TimeoutSeconds));
            });

        return services;
    }
}

/// <summary>
/// Helpers for turning the NSwag client's status-code-only exceptions
/// (the OpenAPI documents only 2xx/404 for several ops, so non-2xx surfaces
/// as <see cref="JeebStateServiceApiException"/>) into typed outcomes the
/// gateway's domain semantics can branch on. Centralised so every decorator
/// maps codes the same way.
/// </summary>
public static class StateServiceErrors
{
    /// <summary>True when the exception represents the given HTTP status.</summary>
    public static bool IsStatus(Exception ex, int statusCode) =>
        ex is JeebStateServiceApiException api && api.StatusCode == statusCode;

    public static bool IsNotFound(Exception ex) => IsStatus(ex, (int)HttpStatusCode.NotFound);

    /// <summary>409 — used for reuse-detected, lock-held, and version-conflict.</summary>
    public static bool IsConflict(Exception ex) => IsStatus(ex, (int)HttpStatusCode.Conflict);

    public static bool IsGone(Exception ex) => IsStatus(ex, (int)HttpStatusCode.Gone);
}
