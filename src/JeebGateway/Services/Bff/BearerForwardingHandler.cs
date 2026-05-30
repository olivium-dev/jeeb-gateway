using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.Services.Bff;

/// <summary>
/// JEB-67 / T-BE-031 — propagates the inbound mobile JWT bearer to every
/// downstream call made by a named HttpClient that registers this handler.
///
/// Why this exists: the gateway is the BFF entry-point. When the mobile app
/// authenticates against the gateway, the resulting bearer token must travel
/// to whichever downstream service the gateway calls so that user-scoped
/// authorization decisions are made against the original principal — NOT
/// against a synthetic service identity that loses tenancy.
///
/// Cremat / salehly / rahmah gateways do NOT do this today (Atlas §1 anti-
/// pattern); they replay an X-User-Id header instead, which means downstream
/// auth is gateway-trusted and any compromised gateway path can impersonate
/// any user. Jeeb fixes that with this handler.
///
/// AC3 (Given mobile passes a JWT, When the gateway calls a downstream
/// microservice, Then bearer is forwarded). The companion
/// <see cref="ServiceAuthSigningHandler"/> covers the "+ ServiceAuth signed"
/// half of the same AC.
///
/// Design notes:
/// - The handler is no-op when there is no inbound HttpContext (background
///   jobs, health checks, retry workers). Forwarding nothing is correct
///   because those callers run under a service identity, not a user.
/// - The handler does NOT overwrite an Authorization header already set by
///   the downstream client (e.g. a static bearer for a service that does not
///   accept user JWTs). Per-client opt-out is via not registering the
///   handler.
/// </summary>
public sealed class BearerForwardingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public BearerForwardingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            var bearer = ExtractInboundBearer();
            if (!string.IsNullOrEmpty(bearer))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    private string? ExtractInboundBearer()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            return null;
        }

        if (!ctx.Request.Headers.TryGetValue("Authorization", out var values))
        {
            return null;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            // Header is "Bearer <token>"; tolerate case-insensitive scheme and
            // any single-space separator. We must not split on whitespace
            // because JWTs themselves contain no whitespace but a careless
            // call to .Split() would silently truncate signed payloads in
            // future variants.
            const string scheme = "Bearer ";
            if (raw.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                var token = raw.Substring(scheme.Length).Trim();
                if (token.Length > 0)
                {
                    return token;
                }
            }
        }

        return null;
    }
}
