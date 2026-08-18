using System.Net.Http.Headers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// Staging-only composite readiness gate for the mandatory settlement owner. Unlike the generic
/// URL probe, this check is registered even when configuration is absent, validates the mounted
/// SERVICE credential, and probes the exact upstream readiness route. A missing owner can therefore
/// never disappear from the readiness roster and false-green the gateway.
/// </summary>
public sealed class SettlementServiceReadinessHealthCheck(
    IOptionsMonitor<SettlementServiceOptions> options,
    IHttpClientFactory clients) : IHealthCheck
{
    internal const string HttpClientName = "settlement-service-readiness";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var current = options.CurrentValue;
        var baseUrl = current.BaseUrl;
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return HealthCheckResult.Unhealthy(
                $"{SettlementServiceOptions.BaseUrlKey} must be an absolute HTTP(S) URL in Staging.");
        }

        string token;
        try
        {
            token = await SettlementServiceTokenHandler.ReadTokenAsync(
                current,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return HealthCheckResult.Unhealthy(
                "Settlement SERVICE credential is missing or invalid in Staging.");
        }

        try
        {
            var root = new Uri(baseUri.ToString().TrimEnd('/') + "/", UriKind.Absolute);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root, "health/ready"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("settlement-service configuration and readiness are valid")
                : HealthCheckResult.Unhealthy(
                    $"settlement-service readiness returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return HealthCheckResult.Unhealthy("settlement-service readiness probe failed.");
        }
    }
}
