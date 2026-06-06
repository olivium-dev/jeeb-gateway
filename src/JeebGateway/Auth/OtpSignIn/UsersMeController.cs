using JeebGateway.Services;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using UmServiceClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// S02 Wave-1 (ADR-003) dual-role BFF surfaces. THIN: identity, persistence and the
/// token-reissuing role switch live in user-management; this controller orchestrates,
/// translates vocabulary (opaque {customer,driver} &lt;-&gt; Jeeb contract {client,jeeber}),
/// and — on the switch path — returns the UM-issued token VERBATIM (signs nothing, N11).
///
/// <list type="bullet">
///   <item><description>F-B <c>GET /v1/users/me</c> — was 404. userId from the BEARER (never the
///     body, I4); reads roles from the validated session claims and display fields from the UM
///     profile; translates to snake_case; 30 s cache-aside; the UM profile failure that used to
///     surface as a raw 500 is now wrapped to RFC 7807.</description></item>
///   <item><description>F-A <c>POST /v1/users/me/role/switch</c> — was 404 fleet-wide. Whitelist
///     ({client,jeeber}); an unknown role (e.g. <c>admin</c>) is <b>400 invalid_role</b> with NO UM
///     call (N6); otherwise translate -&gt; BR-1 guard -&gt; forward to UM role/switch, which
///     persists + RE-ISSUES; the gateway returns the UM token without signing (CP-C / N11). UM's
///     <c>role_not_available</c> maps to <b>403</b> (N5/ALT-1, distinct from the 400).</description></item>
/// </list>
/// Gated by <c>FeatureFlags:UseUpstream:UserManagement</c>: both routes are net-new (404 today),
/// so when the flag is off they fail closed with 503 — there is no legacy behavior to preserve.
/// </summary>
[ApiController]
[Route("v1/users/me")]
[Produces("application/json", "application/problem+json")]
public sealed class UsersMeController : ControllerBase
{
    private const int ProfileCacheSeconds = 30;

    private readonly IUserManagementDualRoleClient _userManagement;
    private readonly UmServiceClient _umProfile;
    private readonly IDualRoleService _dualRole;
    private readonly IUsersStore _users;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<UsersMeController> _log;

    public UsersMeController(
        IUserManagementDualRoleClient userManagement,
        UmServiceClient umProfile,
        IDualRoleService dualRole,
        IUsersStore users,
        IMemoryCache cache,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<UsersMeController> log)
    {
        _userManagement = userManagement;
        _umProfile = umProfile;
        _dualRole = dualRole;
        _users = users;
        _cache = cache;
        _flags = flags;
        _log = log;
    }

    // -----------------------------------------------------------------
    // F-B — GET /v1/users/me
    // -----------------------------------------------------------------

    [HttpGet]
    [ProducesResponseType(typeof(UsersMeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        if (!_flags.CurrentValue.UserManagement)
            return UpstreamDisabled();

        // I4 — identity ALWAYS from the bearer, NEVER a body/query param.
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        // Roles come from the validated session token (the OTP-mint embeds the OPAQUE
        // roles/active_role). Translate to the snake_case contract. G3 mitigation: the
        // active_role read here is at most ProfileCacheSeconds stale after a switch (the
        // switch invalidates the cache; full access-token denylist deferred).
        var opaqueRoles = UserIdentity.GetRoles(HttpContext);
        var contractRoles = JeebRoleTranslator.ToContract(opaqueRoles);
        var opaqueActive = HttpContext.User?.FindFirst("active_role")?.Value;
        var contractActive = JeebRoleTranslator.ToContract(opaqueActive);

        var cacheKey = ProfileCacheKey(userId);
        if (!_cache.TryGetValue(cacheKey, out ProfileDisplay? display))
        {
            // The dual-role identity (the load-bearing snake_case roles) comes from the
            // validated session, NOT the UM profile read — so the UM display fields are
            // BEST-EFFORT. F-B fix: the live /api/User/profile 500 no longer escapes as a
            // raw 500 nor 502s the whole call; a failed display read degrades to null
            // display and the identity is still served. (RFC 7807 is reserved for genuine
            // identity failures, which on this path only come from the bearer = 401.)
            try
            {
                var profile = await _umProfile.ProfileAsync(userId);
                display = new ProfileDisplay(profile?.Username, profile?.Email, profile?.ProfilePic);
            }
            catch (UmApiException ex)
            {
                _log.LogWarning("v1/users/me UM profile read failed: status {Status}", ex.StatusCode);
                display = null;
            }
            catch (Exception ex)
            {
                // Connection refused / timeout / serialization — never let a display blip
                // turn a valid session into a 500. This is the exact fix for the live
                // profile-500 the mobile app hit on GET profile.
                _log.LogWarning(ex, "v1/users/me UM profile read errored; serving identity without display fields");
                display = null;
            }
            _cache.Set(cacheKey, display, TimeSpan.FromSeconds(ProfileCacheSeconds));
        }

        return Ok(new UsersMeResponse
        {
            UserId = userId,
            ActiveRole = string.IsNullOrWhiteSpace(contractActive)
                ? JeebRoleTranslator.ContractClient
                : contractActive,
            AvailableRoles = contractRoles.Length > 0
                ? contractRoles
                : new[] { JeebRoleTranslator.ContractClient },
            Name = display?.Name,
            Email = display?.Email,
            AvatarUrl = display?.AvatarUrl,
        });
    }

    // -----------------------------------------------------------------
    // F-A — POST /v1/users/me/role/switch
    // -----------------------------------------------------------------

    [HttpPost("role/switch")]
    [ProducesResponseType(typeof(RoleSwitchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SwitchRole([FromBody] RoleSwitchRequestDto? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.UserManagement)
            return UpstreamDisabled();

        // I4 — identity from the bearer, never the body.
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        // N6 — gateway-local whitelist. An unknown role (admin, garbage, empty) is
        // 400 invalid_role and MUST NOT touch user-management. ALT-3: client is allowed.
        var opaqueRole = JeebRoleTranslator.ToOpaque(body?.Role);
        if (opaqueRole is null)
        {
            return Problem(
                StatusCodes.Status400BadRequest, "invalid_role",
                "Invalid role",
                $"Role '{body?.Role}' is not a valid Jeeb role. Expected one of: "
                + $"{JeebRoleTranslator.ContractClient}, {JeebRoleTranslator.ContractJeeber}.");
        }

        // BR-1 guard — refuse a switch that would orphan an active delivery under the
        // current role. Validated locally before the UM call; UM owns the persistence,
        // the gateway owns the same-delivery business rule (component seam).
        var br1 = await _dualRole.ValidateRoleSwitchAsync(userId, opaqueRole, ct);
        if (!br1.IsAllowed
            && br1.DenialReason is { } reason
            && reason.Contains("active delivery", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(
                StatusCodes.Status409Conflict, "role_switch_blocked",
                "Role switch blocked",
                reason);
        }

        try
        {
            // UM persists the OPAQUE active_role AND re-issues the token pair. The gateway
            // forwards the result VERBATIM and signs NOTHING (CP-C / N11).
            var reissue = await _userManagement.RoleSwitchAsync(userId, opaqueRole, ct);

            // Keep the local projection (and any cached active_role) consistent with UM.
            await _users.SwitchRoleAsync(userId, reissue.ActiveRole, ct);
            _cache.Remove(ProfileCacheKey(userId));

            var available = await ResolveAvailableRolesAsync(userId, ct);
            _log.LogInformation("v1/users/me/role/switch ok userId={UserId} newRole={Role}", userId, reissue.ActiveRole);

            var contractActive = JeebRoleTranslator.ToContract(reissue.ActiveRole);
            var contractAvailable = JeebRoleTranslator.ToContract(available);

            return Ok(new RoleSwitchResponseDto
            {
                UserId = reissue.UserId,
                ActiveRole = contractActive,
                AvailableRoles = contractAvailable,
                AccessToken = reissue.AccessToken,
                RefreshToken = reissue.RefreshToken,
                User = new RoleSwitchUserBlock
                {
                    UserId = reissue.UserId,
                    ActiveRole = contractActive,
                    AvailableRoles = contractAvailable,
                },
            });
        }
        catch (UserManagementRoleNotAvailableException)
        {
            // N5 / ALT-1 — distinct from the gateway-local 400 invalid_role.
            return Problem(
                StatusCodes.Status403Forbidden, "role_not_available",
                "Role not available",
                "Your account does not hold the requested role.");
        }
        catch (UserManagementCallException ex)
        {
            _log.LogWarning("v1/users/me/role/switch UM failure: status {Status}", ex.StatusCode);
            return Problem(
                StatusCodes.Status502BadGateway, "upstream_fault",
                "Role switch upstream failure",
                "The user-management service returned an unexpected status while switching roles.");
        }
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>Available roles for the response: prefer the local projection, fall back to the session claims.</summary>
    private async Task<IReadOnlyList<string>> ResolveAvailableRolesAsync(string userId, CancellationToken ct)
    {
        var profile = await _users.GetByIdAsync(userId, ct);
        if (profile is { Roles.Count: > 0 }) return profile.Roles;
        return UserIdentity.GetRoles(HttpContext);
    }

    private static string ProfileCacheKey(string userId) => $"v1:users:me:profile:{userId}";

    private ObjectResult UpstreamDisabled() => Problem(
        StatusCodes.Status503ServiceUnavailable, "user_management_unavailable",
        "User-management not enabled",
        "The dual-role identity surface requires user-management orchestration "
        + "(FeatureFlags:UseUpstream:UserManagement is false).");

    private ObjectResult Problem(int status, string shortType, string title, string detail)
        => OtpSignInProblems.UsersProblem(this, status, shortType, title, detail);

    private sealed record ProfileDisplay(string? Name, string? Email, string? AvatarUrl);
}
