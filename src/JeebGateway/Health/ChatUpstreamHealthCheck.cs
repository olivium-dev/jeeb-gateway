using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>Makes chat's real state visible on <c>/health/ready</c>: with the flag off every
/// chat route 503s, and chat-service does serve health routes. See docs/runbooks/chat-activation-and-readiness.md.</summary>
public sealed class ChatUpstreamHealthCheck(
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory) : IHealthCheck
{
    internal const string Name = "chat-upstream-readiness";
    internal const string HttpClientName = "ChatUpstreamReadiness";
    internal const string BaseUrlConfigurationKey = "ChatServiceApi:BaseUrl";
    internal const string FlagConfigurationKey = "FeatureFlags:UseUpstream:Chat";
    internal const string FirestoreProbePath = "api/Health/firebase";
    internal const string LivenessProbePath = "api/Health/check";
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue(FlagConfigurationKey, false))
        {
            return HealthCheckResult.Degraded(
                $"chat disabled by flag ({FlagConfigurationKey}=false): every "
                + "/v1/conversations/* and /v1/realtime/*:chat:* route returns 503");
        }

        if (string.IsNullOrWhiteSpace(configuration[BaseUrlConfigurationKey]))
        {
            return HealthCheckResult.Degraded(
                $"chat is enabled but {BaseUrlConfigurationKey} is not configured");
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            var firestore = await ProbeAsync(client, FirestoreProbePath, budget.Token);
            if (firestore == HttpStatusCode.OK)
            {
                return HealthCheckResult.Healthy(
                    $"chat-service {FirestoreProbePath} passed (Firestore reachable)");
            }

            // A post-#118 chat-service answers 204. Unhealthy would restart-loop the gateway
            // via the Dockerfile HEALTHCHECK, so accept every 2xx and state the weaker proof.
            if (IsSuccess(firestore))
            {
                return HealthCheckResult.Healthy(
                    $"chat-service {FirestoreProbePath} returned {(int)firestore}; "
                    + $"Firestore round-trip UNVERIFIED (legacy {(int)firestore})");
            }

            if (firestore != HttpStatusCode.NotFound)
            {
                return HealthCheckResult.Unhealthy(
                    $"chat-service {FirestoreProbePath} returned {(int)firestore}");
            }

            // An older chat-service predates the Firestore probe (#116/#118).
            var liveness = await ProbeAsync(client, LivenessProbePath, budget.Token);
            return IsSuccess(liveness)
                ? HealthCheckResult.Degraded(
                    $"chat-service has no {FirestoreProbePath} route (404) on this build; "
                    + $"fell back to {LivenessProbePath}, which passed — Firestore is UNVERIFIED")
                : HealthCheckResult.Unhealthy(
                    $"chat-service {FirestoreProbePath} is absent (404) and "
                    + $"{LivenessProbePath} returned {(int)liveness}");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy(
                $"chat-service readiness probe exceeded the {Budget.TotalSeconds:0}s budget");
        }
        catch (Exception ex) when (ex is HttpRequestException
                                  or InvalidOperationException
                                  or IOException
                                  or UriFormatException)
        {
            return HealthCheckResult.Unhealthy("chat-service readiness probe could not be completed");
        }
    }

    private static bool IsSuccess(HttpStatusCode status) => (int)status is >= 200 and <= 299;

    private static async Task<HttpStatusCode> ProbeAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return response.StatusCode;
    }
}
