using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.Auth;

/// <summary>
/// ADR-004 (Directive 1) — uniform gateway-audience authorization applied as the
/// ASP.NET Core <c>FallbackPolicy</c> so that EVERY endpoint requires an identified
/// caller by default, and no controller is silently anonymous. Endpoints that are
/// public by design opt out explicitly with <c>[AllowAnonymous]</c> (the token mint,
/// OTP request/verify, /health*, /metrics, swagger, and the dev/seed routes).
///
/// "Identified caller" is the gateway's existing dual MVP identity model, mirrored
/// verbatim from <see cref="JeebGateway.Users.UserIdentity.TryGetUserId"/>:
/// <list type="number">
///   <item>a validated <c>GatewayBearer</c> principal (iss=jeeb-gateway / aud=jeeb-clients),
///         i.e. the single session audience from ADR-004; OR</item>
///   <item>the edge-injected <c>X-User-Id</c> header (the admin/edge path the directive
///         explicitly requires us to preserve).</item>
/// </list>
///
/// This is NOT a bare <c>RequireAuthenticatedUser()</c>: that would 401 every endpoint
/// that the trusted edge drives via <c>X-User-Id</c> without a bearer. By accepting the
/// same two signals the rest of the gateway already trusts, the fallback makes the
/// audience-auth approach UNIFORM without changing any legitimate flow.
///
/// Note this does NOT relax the explicit <c>[Authorize]</c> routes: those continue to
/// run under the <c>DefaultPolicy</c> (GatewayBearer-only), so a <c>aud=user-management</c>
/// token on <c>/v1/users/me</c> is still 401 (E4b / N5). The fallback only governs the
/// endpoints that previously had NO authorization metadata at all.
/// </summary>
public sealed class GatewayAudienceRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Authorization handler for <see cref="GatewayAudienceRequirement"/>. Succeeds when the
/// caller is identified by a validated gateway-session principal OR by the trusted edge
/// <c>X-User-Id</c> header; otherwise the requirement is left unmet and the framework
/// returns 401.
/// </summary>
public sealed class GatewayAudienceHandler : AuthorizationHandler<GatewayAudienceRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public GatewayAudienceHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        GatewayAudienceRequirement requirement)
    {
        // (1) A validated bearer principal authenticated under the gateway scheme.
        if (context.User?.Identity?.IsAuthenticated == true)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        // (2) The by-design edge path: a trusted X-User-Id header injected by the edge.
        // Mirrors UserIdentity.TryGetUserId so the fallback authorizes exactly the set of
        // callers the gateway already treats as identified — nothing more, nothing less.
        var http = _httpContextAccessor.HttpContext;
        if (http is not null
            && http.Request.Headers.TryGetValue("X-User-Id", out var header)
            && !string.IsNullOrWhiteSpace(header))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
