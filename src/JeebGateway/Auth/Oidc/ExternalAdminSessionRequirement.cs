using System.Globalization;
using JeebGateway.Auth.Capabilities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;

namespace JeebGateway.Auth.Oidc;

/// <summary>
/// The additional trust boundary on every <c>admin.*</c> capability. Operator
/// APIs accept only a gateway-signed bearer produced from the configured OIDC
/// ceremony; ordinary gateway/mobile tokens and trusted-edge headers are not
/// administrator sessions.
/// </summary>
public sealed class ExternalAdminSessionRequirement : IAuthorizationRequirement
{
    public const string GatewayBearerScheme = "GatewayBearer";
    public const string SessionClaim = "operator_session";
    public const string SessionClaimValue = "external_oidc";
}

public sealed class ExternalAdminSessionAuthorizationHandler
    : AuthorizationHandler<ExternalAdminSessionRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;

    public ExternalAdminSessionAuthorizationHandler(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ExternalAdminSessionRequirement requirement)
    {
        var http = _httpContextAccessor.HttpContext;
        if (http is null) return;

        // Extraction adaptation: until AdminOidc is provisioned the boundary
        // collapses to the existing role-capability layers (legacy admin reads).
        if (!AdminOidcOptions.TryLoad(_configuration, out var options, out _))
        {
            context.Succeed(requirement);
            return;
        }

        // Authenticate this exact scheme. Do not accept an arbitrary authenticated
        // ClaimsPrincipal or the GatewayAudienceRequirement's trusted-edge path.
        var gateway = await http.AuthenticateAsync(
            ExternalAdminSessionRequirement.GatewayBearerScheme);
        var principal = gateway.Succeeded ? gateway.Principal : null;
        if (principal is null
            || !string.Equals(
                principal.FindFirst(ExternalAdminSessionRequirement.SessionClaim)?.Value,
                ExternalAdminSessionRequirement.SessionClaimValue,
                StringComparison.Ordinal)
            || !string.Equals(
                principal.FindFirst("idp")?.Value,
                options.Issuer,
                StringComparison.Ordinal)
            || !principal.FindAll("amr").Any(claim =>
                string.Equals(claim.Value, "mfa", StringComparison.OrdinalIgnoreCase))
            || !long.TryParse(
                principal.FindFirst("auth_time")?.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var authTime)
            || authTime <= 0)
            return;

        context.Succeed(requirement);
    }
}

internal static class AdminCapabilityBoundary
{
    internal static bool RequiresExternalOperatorSession(string capability) =>
        capability.StartsWith("admin.", StringComparison.Ordinal);
}
