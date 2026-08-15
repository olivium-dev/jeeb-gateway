using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Read-only administrator session projection. The browser uses the returned
/// capabilities only to narrow navigation and controls; every operation is
/// independently protected by the same capability policies in the gateway.
/// </summary>
[ApiController]
[Route("admin/v1/session")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("admin/session")]
public sealed class AdminSessionController : ControllerBase
{
    [HttpGet]
    [RequireCapability(Capabilities.AdminPortalAccess)]
    [ProducesResponseType(typeof(AdminSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public IActionResult Get()
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
            return unauthorized;

        var roles = UserIdentity.GetRoles(HttpContext)
            .Select(JeebRoleTranslator.ToContract)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var capabilities = CapabilityRolePolicy.Map
            .Where(pair => pair.Key.StartsWith("admin.", StringComparison.Ordinal)
                && roles.Any(role => pair.Value.Contains(role, StringComparer.OrdinalIgnoreCase)))
            .Select(static pair => pair.Key)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

        var authTime = ReadUnixTimeClaim("auth_time") ?? ReadUnixTimeClaim(JwtRegisteredClaimNames.Iat);
        var methods = User.FindAll("amr")
            .SelectMany(static claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var mfaSatisfied = methods.Any(method =>
            string.Equals(method, "mfa", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "otp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(method, "webauthn", StringComparison.OrdinalIgnoreCase));

        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(new AdminSessionResponse(
            userId,
            User.FindFirst("name")?.Value,
            User.FindFirst("email")?.Value,
            roles,
            capabilities,
            authTime,
            methods,
            mfaSatisfied,
            new AdminCaseClosurePolicy(
                CustomerConfirmationRequired: false,
                RequiredCapability: Capabilities.AdminCasesClose)));
    }

    private DateTimeOffset? ReadUnixTimeClaim(string name)
    {
        var raw = User.FindFirst(name)?.Value;
        return long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }
}

public sealed record AdminSessionResponse(
    string UserId,
    string? DisplayName,
    string? Email,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset? AuthenticatedAt,
    IReadOnlyList<string> AuthenticationMethods,
    bool MfaSatisfied,
    AdminCaseClosurePolicy ClosurePolicy);

public sealed record AdminCaseClosurePolicy(
    bool CustomerConfirmationRequired,
    string RequiredCapability);
