using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Cms;

/// <summary>
/// Readiness probe for bundler-service that is CAPABLE OF FAILING. It dials the same named client
/// <see cref="BundlerCmsSurfaceStore"/> uses, hits bundler's own <c>health/ready</c>, and requires a
/// non-empty body: a Host-matching reverse proxy answers 200 with <c>Content-Length: 0</c> on every
/// path when no site block matches, so a status code alone proved nothing.
/// Registered Degraded, not Unhealthy — bundler backs the ADMIN authoring plane only.
/// </summary>
public sealed class BundlerServiceHealthCheck : IHealthCheck
{
    /// <summary>Declared in <see cref="Extensions.GatewayHealthRoster"/>; do not rename.</summary>
    public const string Name = "bundler-service";

    private const string ReadyPath = "health/ready";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration? _configuration;

    public BundlerServiceHealthCheck(
        IHttpClientFactory httpClientFactory, IConfiguration? configuration = null)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
    }

    /// <summary>
    /// Names the configuration provider whose value actually won for <paramref name="key"/>.
    /// Later providers override earlier ones, so the LAST match is the effective one.
    /// </summary>
    internal static string DescribeSource(IConfiguration? configuration, string key)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return "unknown";
        }

        string? winner = null;
        foreach (var provider in root.Providers)
        {
            if (provider.TryGet(key, out _))
            {
                winner = provider.GetType().Name;
            }
        }

        return winner ?? "unset";
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var failure = context.Registration.FailureStatus;

        HttpClient client;
        try
        {
            client = _httpClientFactory.CreateClient(BundlerCmsSurfaceStore.HttpClientName);
        }
        catch (Exception ex)
        {
            // The named client's factory throws when BUNDLER_CMS_BASE_URL is not an absolute URL.
            return new HealthCheckResult(
                failure, "bundler-service client is not configured: " + ex.Message, ex);
        }

        var probed = client.BaseAddress is null
            ? ReadyPath
            : new Uri(client.BaseAddress, ReadyPath).ToString();
        // Two config layers can set this key and the loser is invisible from outside.
        var source = DescribeSource(
            _configuration, BundlerCmsSurfaceStore.BaseUrlConfigurationKey);
        var origin = $"{probed} (from {BundlerCmsSurfaceStore.BaseUrlConfigurationKey}"
                     + $" via {source})";
        var data = new Dictionary<string, object>
        {
            ["probedUrl"] = probed,
            ["baseUrlSource"] = source,
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        try
        {
            using var response = await client.GetAsync(ReadyPath, budget.Token);
            var body = await response.Content.ReadAsByteArrayAsync(budget.Token);
            data["statusCode"] = (int)response.StatusCode;
            data["bodyBytes"] = body.Length;

            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult(
                    failure,
                    $"bundler-service answered {(int)response.StatusCode} at {origin}",
                    data: data);
            }

            if (body.Length == 0)
            {
                return new HealthCheckResult(
                    failure,
                    $"bundler-service answered 200 with an EMPTY body at {origin}: no bundler site "
                    + "block matched this host, so the CMS routes reach a proxy default",
                    data: data);
            }

            return HealthCheckResult.Healthy($"bundler-service is ready at {origin}", data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                failure, $"bundler-service is unreachable at {origin}", ex, data);
        }
    }
}
