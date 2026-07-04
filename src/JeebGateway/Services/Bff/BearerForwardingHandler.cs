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

    private const string CorrelationIdHeader = "X-Correlation-Id";

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

        // GW12-OBS-3 (Leg-12): forward the inbound X-Correlation-Id onto the outbound
        // downstream call so an on-call engineer can grep ONE id across gateway +
        // downstream logs. W3C traceparent propagates automatically via HttpClient
        // instrumentation, but the human-readable correlation id a client/QA actually
        // quotes did not survive the gateway→downstream hop until now. This handler is
        // the universal first handler on every named downstream client (BearerForwarding
        // → ServiceAuthSigning), so forwarding here covers all of them; the Contains
        // guard keeps it idempotent and never overwrites an explicitly-set header.
        if (!request.Headers.Contains(CorrelationIdHeader))
        {
            var correlationId = ExtractInboundCorrelationId();
            if (!string.IsNullOrEmpty(correlationId))
            {
                request.Headers.TryAddWithoutValidation(CorrelationIdHeader, correlationId);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }

    private string? ExtractInboundCorrelationId()
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx is null)
        {
            return null;
        }

        // Prefer the value CorrelationIdMiddleware stashed (it mints one when the client
        // omits the header, so this is populated for every gateway-originated request);
        // fall back to the raw inbound header for calls that bypass the middleware.
        if (ctx.Items.TryGetValue("CorrelationId", out var stashed) && stashed is string s && s.Length > 0)
        {
            return s;
        }

        if (ctx.Request.Headers.TryGetValue(CorrelationIdHeader, out var values))
        {
            var header = values.ToString();
            if (!string.IsNullOrWhiteSpace(header))
            {
                return header;
            }
        }

        return null;
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
