using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// Attaches the SERVICE-scope bearer token settlement-service expects (NotificationServiceTokenHandler
/// shape). The admin scope — batches, mark-paid, diagnostics — is deliberately never configured here:
/// a leaked gateway token must not be able to pay anyone.
/// </summary>
public sealed class SettlementServiceTokenHandler : DelegatingHandler
{
    private readonly IOptionsMonitor<SettlementServiceOptions> _options;

    public SettlementServiceTokenHandler(IOptionsMonitor<SettlementServiceOptions> options)
    {
        _options = options;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = _options.CurrentValue.ApiToken;
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
