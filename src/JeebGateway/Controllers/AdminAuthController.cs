using System.Security.Cryptography;
using JeebGateway.Auth;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace JeebGateway.Controllers;

/// <summary>
/// Browser-safe administrator session refresh and logout facade. Initial
/// administrator authentication is exclusively the external OIDC flow; the
/// gateway keeps the rotating refresh credential out of JavaScript in a
/// host-only cookie.
/// </summary>
[ApiController]
[Route("admin/v1/auth")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("admin/auth")]
[AllowAnonymous]
[PublicEndpoint("Admin login and cookie rotation authenticate before a bearer exists.")]
[EnableRateLimiting(RateLimitingExtensions.AuthTokenBucketPolicy)]
public sealed class AdminAuthController : ControllerBase
{
    internal const string RefreshCookie = AdminSessionCookies.RefreshCookie;
    internal const string CsrfCookie = AdminSessionCookies.CsrfCookie;
    internal const string CsrfHeader = AdminSessionCookies.CsrfHeader;

    private readonly IUserManagementDualRoleClient _roles;
    private readonly IDevSeededRoleStore _seededRoles;
    private readonly ITokenService _tokens;
    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;

    public AdminAuthController(
        IUserManagementDualRoleClient roles,
        IDevSeededRoleStore seededRoles,
        ITokenService tokens,
        IWebHostEnvironment environment,
        IConfiguration configuration)
    {
        _roles = roles;
        _seededRoles = seededRoles;
        _tokens = tokens;
        _environment = environment;
        _configuration = configuration;
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(AdminAccessTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh(CancellationToken ct)
    {
        PreventCaching();
        if (!IsSameOriginRequest()) return OriginRejected();
        if (!HasValidCsrfToken()) return ProblemResult(403, "csrf_rejected", "The session request was rejected.");
        if (!Request.Cookies.TryGetValue(RefreshCookie, out var refresh) || string.IsNullOrWhiteSpace(refresh))
            return ProblemResult(401, "invalid_refresh", "Sign in again.");

        var result = await _tokens.RefreshAsync(refresh, ResolveAdminRolesAsync, ct);
        if (result.Outcome == RefreshOutcome.RoleResolutionFailed)
            return ProblemResult(503, "identity_unavailable", "Administrator roles could not be verified.");
        if (result.Outcome != RefreshOutcome.Ok || result.Tokens is null)
        {
            DeleteSessionCookies();
            return ProblemResult(401, "invalid_refresh", "Sign in again.");
        }

        SetSessionCookies(result.Tokens.RefreshToken, rotateCsrf: false);
        Response.Headers.CacheControl = "no-store";
        return Ok(new AdminAccessTokenResponse(result.Tokens.AccessToken));
    }

    private async Task<TokenRoleContext?> ResolveAdminRolesAsync(string userId, CancellationToken ct)
    {
        var result = await _roles.GetUserRolesAsync(userId, ct);
        var roles = (result?.AvailableRoles ?? Array.Empty<string>())
            .Union(_seededRoles.Resolve(userId, null) ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase)
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!HasPortalAccess(roles)) return null;
        var activeRole = result?.ActiveRole;
        if (string.IsNullOrWhiteSpace(activeRole) || !roles.Contains(activeRole, StringComparer.OrdinalIgnoreCase))
            activeRole = roles[0];
        return new TokenRoleContext(roles, activeRole);
    }

    [HttpPost("logout")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        PreventCaching();
        if (!IsSameOriginRequest()) return OriginRejected();
        if (!HasValidCsrfToken()) return ProblemResult(403, "csrf_rejected", "The session request was rejected.");
        try
        {
            if (Request.Cookies.TryGetValue(RefreshCookie, out var refresh)
                && !string.IsNullOrWhiteSpace(refresh))
                await _tokens.RevokeAsync(refresh, RevocationReason.Logout, ct);
        }
        finally
        {
            // Revocation is authoritative server-side cleanup, but a transient
            // state-service failure must never leave browser credentials behind.
            DeleteSessionCookies();
        }
        return NoContent();
    }

    private static bool HasPortalAccess(IEnumerable<string> roles)
    {
        var allowed = CapabilityRolePolicy.RolesFor(Capabilities.AdminPortalAccess);
        return roles.Select(JeebRoleTranslator.ToContract)
            .Any(role => allowed.Contains(role, StringComparer.OrdinalIgnoreCase));
    }

    private bool IsSameOriginRequest()
    {
        if (Request.Headers.TryGetValue("Sec-Fetch-Site", out var fetchSite)
            && !string.Equals(fetchSite.ToString(), "same-origin", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(fetchSite.ToString(), "none", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!Request.Headers.TryGetValue("Origin", out var origin) || string.IsNullOrWhiteSpace(origin))
            return _environment.IsDevelopment() || _environment.IsEnvironment("Testing");

        if (Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var supplied)
            && string.Equals(supplied.Scheme, Request.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(supplied.Authority, Request.Host.Value, StringComparison.OrdinalIgnoreCase))
            return true;

        var allowed = _configuration.GetSection("AdminPortal:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
        return allowed.Contains(origin.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    private bool HasValidCsrfToken()
    {
        if (!Request.Cookies.TryGetValue(CsrfCookie, out var cookie)
            || !Request.Headers.TryGetValue(CsrfHeader, out var header)) return false;
        var left = System.Text.Encoding.UTF8.GetBytes(cookie);
        var right = System.Text.Encoding.UTF8.GetBytes(header.ToString());
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }

    private void SetSessionCookies(string refreshToken, bool rotateCsrf = true)
        => AdminSessionCookies.Set(Request, Response, refreshToken, rotateCsrf);

    private void DeleteSessionCookies()
        => AdminSessionCookies.Delete(Response);

    private ObjectResult OriginRejected() => ProblemResult(403, "origin_rejected", "The session request was rejected.");

    private void PreventCaching()
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
    }

    private ObjectResult ProblemResult(int status, string type, string detail)
    {
        PreventCaching();
        return new ObjectResult(new ProblemDetails
        {
            Status = status,
            Type = $"https://jeeb.dev/errors/{type}",
            Title = type,
            Detail = detail,
            Instance = Request.Path,
        })
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}

public sealed record AdminAccessTokenResponse(string AccessToken);
