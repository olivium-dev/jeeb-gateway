using System.Net;

namespace JeebGateway.Security;

/// <summary>
/// Network boundary for unauthenticated owner callbacks. Production owners run
/// in containers, so loopback alone is insufficient; explicitly configured
/// private CIDRs are accepted while public browser ingress remains denied.
/// </summary>
public static class PrivateCallbackIngressPolicy
{
    private static readonly IPNetwork[] AllowedOwnerNetworks =
    [
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
    ];

    public static bool IsTrusted(HttpContext http, IConfiguration configuration)
    {
        var address = http.Connection.RemoteIpAddress;
        if (address is null) return false;
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();

        foreach (var value in configuration.GetSection("AdminCallbacks:TrustedNetworks").Get<string[]>()
                     ?? Array.Empty<string>())
        {
            if (!IPNetwork.TryParse(value, out var network)
                || network.BaseAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork
                || !AllowedOwnerNetworks.Any(allowed =>
                    network.PrefixLength >= allowed.PrefixLength && allowed.Contains(network.BaseAddress)))
                continue;
            if (network.Contains(address)) return true;
        }
        return false;
    }
}
