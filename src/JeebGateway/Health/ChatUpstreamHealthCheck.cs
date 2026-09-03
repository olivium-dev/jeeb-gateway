using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>
/// Makes chat's real state visible on <c>/health/ready</c>. Two facts were
/// invisible before this check: (1) with <c>FeatureFlags:UseUpstream:Chat</c>
/// off, every <c>/v1/conversations/*</c> and <c>/v1/realtime/*:chat:*</c> route
/// returns 503 while the gateway reports Healthy; (2) chat-service does serve
/// health routes — the old "no health route" exclusion in
/// <see cref="Extensions.HealthCheckExtensions"/> rested on a wrong premise — and
/// since 2026-09-03 it serves a real Firestore probe at <c>/api/Health/firebase</c>.
/// </summary>
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

            if (firestore != HttpStatusCode.NotFound)
            {
                return HealthCheckResult.Unhealthy(
                    $"chat-service {FirestoreProbePath} returned {(int)firestore}");
            }

            // An older chat-service predates the Firestore probe (#116/#118).
            var liveness = await ProbeAsync(client, LivenessProbePath, budget.Token);
            return liveness == HttpStatusCode.OK
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
