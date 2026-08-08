using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Attaches the static X-Api-Key header role-service's ServiceKeys-tier auth
/// expects. No-op when the key is unconfigured (safe while the flag is off).
/// </summary>
public sealed class RoleServiceApiKeyHandler : DelegatingHandler
{
    private const string HeaderName = "X-Api-Key";

    private readonly IOptionsMonitor<RoleServiceOptions> _options;

    public RoleServiceApiKeyHandler(IOptionsMonitor<RoleServiceOptions> options)
    {
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = _options.CurrentValue.ApiKey;
        if (!string.IsNullOrWhiteSpace(key))
        {
            request.Headers.Remove(HeaderName);
            request.Headers.Add(HeaderName, key);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
