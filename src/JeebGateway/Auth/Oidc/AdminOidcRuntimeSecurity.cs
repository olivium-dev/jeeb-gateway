using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Auth.Oidc;

internal static class AdminOidcStartupGuard
{
    // Enforcement is opt-in (AdminOidc:Enabled) so the live mobile BFF boots with zero new config.
    internal static void EnsureConfigured(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!configuration.GetValue<bool>($"{AdminOidcOptions.SectionName}:Enabled")) return;
        if (environment.IsDevelopment() || environment.IsEnvironment("Testing")) return;
        if (AdminOidcOptions.TryLoad(configuration, out _, out var error)) return;

        throw new InvalidOperationException(
            $"Production external administrator identity is not ready: {error}");
    }
}

internal sealed class AdminOidcConfigurationHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;

    public AdminOidcConfigurationHealthCheck(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        _configuration = configuration;
        _environment = environment;
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (!_configuration.GetValue<bool>($"{AdminOidcOptions.SectionName}:Enabled"))
            return Task.FromResult(HealthCheckResult.Healthy(
                "external administrator identity is disabled (AdminOidc:Enabled=false)"));
        if (_environment.IsDevelopment() || _environment.IsEnvironment("Testing"))
            return Task.FromResult(HealthCheckResult.Healthy(
                "external administrator identity is optional in the local harness"));

        return Task.FromResult(AdminOidcOptions.TryLoad(_configuration, out _, out var error)
            ? HealthCheckResult.Healthy("external administrator identity is configured")
            : HealthCheckResult.Unhealthy(error));
    }
}

/// <summary>
/// A redirect-free, proxy-free IdP transport. The connect callback pins each
/// connection to a DNS answer that is public at connection time, preventing a
/// configured public hostname from rebinding onto loopback/private/link-local
/// address space.
/// </summary>
internal static class AdminOidcHttpTransport
{
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectPublicAsync,
    };

    private static async ValueTask<Stream> ConnectPublicAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(
                context.DnsEndPoint.Host, cancellationToken);
        }
        catch (SocketException exception)
        {
            throw new HttpRequestException("The OIDC provider hostname could not be resolved.", exception);
        }

        Exception? last = null;
        foreach (var address in addresses.Where(AdminOidcEndpointPolicy.IsPublicAddress))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(
                    new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception exception) when (exception is SocketException or OperationCanceledException)
            {
                socket.Dispose();
                last = exception;
                if (exception is OperationCanceledException) throw;
            }
        }

        throw new HttpRequestException(
            "The OIDC provider resolved only to prohibited or unreachable addresses.", last);
    }
}

internal static class AdminOidcEndpointPolicy
{
    internal static bool TryPublicHttpsUri(string value, out Uri? uri)
    {
        string host;
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri)
            || value.Length > 2_048
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(host = uri.Host.Trim('[', ']'))
            || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(host, out var address) && !IsPublicAddress(address))
        {
            uri = null;
            return false;
        }
        return true;
    }

    internal static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase)
        && left.Port == right.Port;

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None)) return false;

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                0 or 10 or 127 => false,
                100 when bytes[1] is >= 64 and <= 127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                >= 224 => false,
                _ => true,
            };
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        if (bytes.Take(8).All(static value => value == 0)) return false; // IPv4-compatible/translatable
        if (bytes[0] == 0x00 && bytes[1] == 0x64 && bytes[2] == 0xff && bytes[3] == 0x9b
            && (bytes[4] == 0x00 && bytes[5] == 0x01 || bytes.Skip(4).Take(8).All(static value => value == 0)))
            return false; // RFC 6052 NAT64 well-known/local-use prefixes
        if (bytes[0] == 0x20 && bytes[1] == 0x02) return false; // 6to4 embeds IPv4
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x00 && bytes[3] == 0x00)
            return false; // Teredo embeds an obfuscated IPv4 endpoint
        if (bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            return false; // documentation-only

        return !address.IsIPv6LinkLocal
               && !address.IsIPv6Multicast
               && !address.IsIPv6SiteLocal
               && (bytes[0] & 0xfe) != 0xfc; // fc00::/7 unique-local
    }
}
