using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace JeebGateway.Security;

/// <summary>
/// F2 (Leg-12) — pre-authorization fail-closed 404 for <c>[DevOnly]</c> endpoints.
///
/// <para>
/// The <see cref="DevOnlyAttribute"/> is an MVC <c>IAsyncActionFilter</c>: it runs
/// INSIDE the endpoint pipeline, which the ASP.NET Core authorization middleware
/// (<c>UseAuthorization</c>) precedes. So for a <c>[DevOnly]</c> controller that is
/// also <c>[Authorize]</c>, an ANONYMOUS request to a disabled dev route was rejected
/// <b>401</b> by the authorization middleware BEFORE the action filter's 404 could run
/// — revealing "this route exists but needs a token" to unauthenticated callers
/// (route-existence disclosure; the comment claiming attribute-order alone yielded a
/// 404 was incorrect — filter order does not move a check ahead of the auth middleware).
/// </para>
///
/// <para>
/// This middleware closes that gap. Registered AFTER <c>UseRouting</c> (so the matched
/// endpoint — and therefore its <c>[DevOnly]</c> metadata — is resolvable) and BEFORE
/// <c>UseAuthentication</c>/<c>UseAuthorization</c>, it short-circuits any endpoint
/// carrying <see cref="DevOnlyAttribute"/> metadata with a bare <b>404</b> whenever
/// <see cref="DevEndpointOptions.Enabled"/> is false — for EVERY caller (anonymous or
/// authenticated), uniformly, before auth can distinguish them. When the flag is ON
/// (the single E2E/dev environment) the middleware is a pure pass-through and changes
/// nothing. The <see cref="DevOnlyAttribute"/> action filter is retained as
/// defence-in-depth for hosts that do not register this middleware.
/// </para>
/// </summary>
public sealed class DevOnlyEndpointGuardMiddleware
{
    private readonly RequestDelegate _next;

    public DevOnlyEndpointGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, IOptionsMonitor<DevEndpointOptions> options)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<DevOnlyAttribute>() is not null
            && !options.CurrentValue.Enabled)
        {
            // Behave as if the route does not exist — 404, not 401/403 — so a disabled
            // dev surface is indistinguishable from "no such endpoint" to unauthenticated
            // and authenticated callers alike. No body: no hint the route is real.
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
    }
}
