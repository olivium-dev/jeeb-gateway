namespace JeebGateway.Security;

// TLS terminates at the edge and the Swarm ingress peer is deliberately not a
// trusted proxy, so the public scheme comes from configuration, not Request.Scheme.
internal static class PublicOriginResolver
{
    public static string ResolveScheme(HttpRequest request, IConfiguration configuration)
    {
        var host = request.Host.Value;
        if (string.IsNullOrWhiteSpace(host)) return request.Scheme;

        foreach (var candidate in ConfiguredOrigins(configuration))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var origin)
                && (origin.Scheme == Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)
                && string.Equals(origin.Authority, host, StringComparison.OrdinalIgnoreCase))
            {
                return origin.Scheme;
            }
        }

        return request.Scheme;
    }

    private static IEnumerable<string> ConfiguredOrigins(IConfiguration configuration)
    {
        var publicBaseUrl = configuration["Gateway:PublicBaseUrl"];
        if (!string.IsNullOrWhiteSpace(publicBaseUrl)) yield return publicBaseUrl;

        var allowed = configuration.GetSection("AdminPortal:AllowedOrigins").Get<string[]>()
            ?? Array.Empty<string>();
        foreach (var origin in allowed)
        {
            if (!string.IsNullOrWhiteSpace(origin)) yield return origin;
        }
    }
}
