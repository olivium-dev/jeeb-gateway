using Microsoft.Extensions.Options;

namespace JeebGateway.Realtime.Proxy;

/// <summary>
/// Dedicated, restart-bound switch and resource bounds for the public Phoenix
/// WebSocket transport. It is deliberately independent from
/// <c>FeatureFlags:UseUpstream:Realtime</c>, which controls the gateway's HTTP
/// realtime client rather than the edge transport.
/// </summary>
public sealed class RealtimeWebSocketProxyOptions
{
    public const string SectionName = "Features:RealtimeWebSocketProxy";

    public bool Enabled { get; set; }

    public int GlobalConcurrencyLimit { get; set; } = 256;

    public int PerIpConcurrencyLimit { get; set; } = 8;

    public int MaximumTrackedClientIps { get; set; } = 4096;

    public int ConnectTimeoutSeconds { get; set; } = 5;

    public int ActivityTimeoutSeconds { get; set; } = 90;

    internal bool HasSafeBounds =>
        GlobalConcurrencyLimit is >= 1 and <= 4096
        && PerIpConcurrencyLimit is >= 1 and <= 64
        && PerIpConcurrencyLimit <= GlobalConcurrencyLimit
        && MaximumTrackedClientIps is >= 64 and <= 65536
        && ConnectTimeoutSeconds is >= 1 and <= 30
        && ActivityTimeoutSeconds is >= 15 and <= 300;
}

internal static class RealtimeWebSocketProxyStartupGuard
{
    public static void EnsureEnvironment(
        RealtimeWebSocketProxyOptions options,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        if (options.Enabled && !environment.IsStaging())
        {
            throw new InvalidOperationException(
                $"{RealtimeWebSocketProxyOptions.SectionName}:Enabled may be true only in Staging.");
        }
    }
}

internal interface IRealtimeProxyDestinationResolver
{
    bool TryResolve(out string destinationPrefix);
}

/// <summary>
/// Accepts only the reviewed Swarm-overlay authority. An enabled but malformed
/// destination remains a request-time 503 so liveness/readiness and unrelated
/// gateway routes stay available while operators repair configuration.
/// </summary>
internal sealed class RealtimeProxyDestinationResolver(
    IOptions<RealtimeGuardianOptions> realtimeOptions,
    IOptions<RealtimeWebSocketProxyOptions> proxyOptions)
    : IRealtimeProxyDestinationResolver
{
    internal const string OverlayHost = "jeeb-staging-realtime-comunication-service";
    internal const int OverlayPort = 4000;

    private readonly RealtimeGuardianOptions _realtimeOptions = realtimeOptions.Value;
    private readonly RealtimeWebSocketProxyOptions _proxyOptions = proxyOptions.Value;

    public bool TryResolve(out string destinationPrefix)
    {
        destinationPrefix = string.Empty;
        if (!_proxyOptions.HasSafeBounds
            || !Uri.TryCreate(_realtimeOptions.BaseUrl, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || !string.Equals(uri.Host, OverlayHost, StringComparison.Ordinal)
            || uri.Port != OverlayPort
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.AbsolutePath != "/" && uri.AbsolutePath.Length != 0))
        {
            return false;
        }

        destinationPrefix = $"http://{OverlayHost}:{OverlayPort}";
        return true;
    }
}
