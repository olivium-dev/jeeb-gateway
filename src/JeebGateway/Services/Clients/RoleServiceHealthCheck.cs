using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Readiness probe for role-service that is CAPABLE OF FAILING. role-service backs
/// <see cref="JeebGateway.Users.RoleServiceBackedDualRoleClient"/>, which every
/// <c>GET /v1/users/me</c> goes through once
/// <c>FeatureFlags:UseUpstream:RoleService</c> is on — yet it carried no readiness
/// probe, so a hard down of it left the aggregate green (see the D1 outage).
///
/// Two failure modes both count as NOT ready:
///   * a non-2xx status (role-service answers 503 when its Postgres is unreachable), and
///   * a 200 with an EMPTY body — a bare listener on this host answers 200 with
///     <c>Content-Length: 0</c>, so a status-only probe is structurally incapable of failing.
///
/// Severity follows the kill switch, matching how the codebase treats every other
/// flag-gated upstream: with the flag ON role-service is live-path and a failure is
/// <see cref="HealthCheckContext.Registration"/>'s failure status (Unhealthy -> 503);
/// with the flag OFF the gateway routes roles straight to user-management, so a
/// failure is reported <see cref="HealthStatus.Degraded"/> (visible, still 200).
/// </summary>
public sealed class RoleServiceHealthCheck : IHealthCheck
{
    /// <summary>Declared in <see cref="Extensions.GatewayHealthRoster"/>; do not rename.</summary>
    public const string Name = "role-service";

    /// <summary>The BaseUrl key the probe and the typed client share.</summary>
    public const string BaseUrlConfigurationKey = RoleServiceOptions.SectionName + ":BaseUrl";

    private const string ReadyPath = "health/ready";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<RoleServiceOptions> _options;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public RoleServiceHealthCheck(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<RoleServiceOptions> options,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _flags = flags;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken ct = default)
    {
        var livePath = _flags.CurrentValue.RoleService;
        // Off the live path a broken role-service cannot serve a 503: user-management answers roles.
        var failure = livePath ? context.Registration.FailureStatus : HealthStatus.Degraded;

        var baseUrl = _options.CurrentValue.BaseUrl;
        var data = new Dictionary<string, object>
        {
            ["livePath"] = livePath,
            ["baseUrlKey"] = BaseUrlConfigurationKey,
        };

        if (string.IsNullOrWhiteSpace(baseUrl)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + "/", UriKind.Absolute, out var root))
        {
            return new HealthCheckResult(
                failure,
                $"role-service has no usable {BaseUrlConfigurationKey}",
                data: data);
        }

        var endpoint = new Uri(root, ReadyPath);
        data["probedUrl"] = endpoint.ToString();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);

        try
        {
            var client = _httpClientFactory.CreateClient();
            using var response = await client.GetAsync(endpoint, budget.Token);
            var body = await response.Content.ReadAsByteArrayAsync(budget.Token);
            data["statusCode"] = (int)response.StatusCode;
            data["bodyBytes"] = body.Length;

            if (!response.IsSuccessStatusCode)
            {
                return new HealthCheckResult(
                    failure,
                    $"role-service answered {(int)response.StatusCode} at {endpoint}",
                    data: data);
            }

            if (body.Length == 0)
            {
                return new HealthCheckResult(
                    failure,
                    $"role-service answered 200 with an EMPTY body at {endpoint}: a status-only "
                    + "probe cannot distinguish this from a bare listener, so it is not a pass",
                    data: data);
            }

            return HealthCheckResult.Healthy($"role-service is ready at {endpoint}", data);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new HealthCheckResult(
                failure, $"role-service is unreachable at {endpoint}", ex, data);
        }
    }
}
