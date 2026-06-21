using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
// Alias the capability registry CLASS: inside the JeebGateway.Auth.* namespace, the bare name
// `Capabilities` binds to the JeebGateway.Auth.Capabilities NAMESPACE, not the class. This alias
// disambiguates so [RequireCapability(Caps.X)] resolves the constant.
using Caps = JeebGateway.Auth.Capabilities.Capabilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using UmServiceClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// S02 dual-role BFF read surface. THIN: identity and persistence live in user-management;
/// this controller orchestrates the read and translates vocabulary (opaque {customer,driver}
/// &lt;-&gt; Jeeb contract {client,jeeber}).
///
/// ADR-004 (upgrade-not-switch): there is NO role-switch ceremony. A client is UPGRADED to
/// jeeber by real S03 KYC approval (the existing GrantRole → available_roles append), and the
/// user's next gateway-minted session token (aud=jeeb-clients) carries their FULL role set —
/// acting as jeeber is just exercising a jeeber-scoped route with that single session token.
/// The former <c>POST /v1/users/me/role/switch</c> action (ADR-003 F-A) is therefore removed.
///
/// <list type="bullet">
///   <item><description>F-B <c>GET /v1/users/me</c> — userId from the BEARER (never the body, I4);
///     reads roles from the validated session claims / local UM projection and display fields from
///     the UM profile; translates to snake_case; 30 s cache-aside; a UM profile read failure
///     degrades to null display rather than surfacing a raw 500.</description></item>
/// </list>
/// Gated by <c>FeatureFlags:UseUpstream:UserManagement</c>: the route is net-new (404 today),
/// so when the flag is off it fails closed with 503 — there is no legacy behavior to preserve.
/// </summary>
[ApiController]
[Route("v1/users/me")]
// ADR-004: enforce the default authorization policy (GatewayBearerScheme only, aud=jeeb-clients).
// Without this, the issuer-routing policy scheme would still establish a UM principal for an
// aud=user-management token and the manual UserIdentity check would let it through. With
// [Authorize] the UM-audience token is rejected 401 at the auth layer (E4b/N5/N7.3). The manual
// UserIdentity.TryGetUserId resolution remains as defense-in-depth + the edge X-User-Id path.
[Authorize]
[Produces("application/json", "application/problem+json")]
public sealed class UsersMeController : ControllerBase
{
    private const int ProfileCacheSeconds = 30;

    private readonly UmServiceClient _umProfile;
    private readonly IUsersStore _users;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IUserManagementDualRoleClient _dualRole;
    private readonly ITokenService _tokens;
    private readonly ILogger<UsersMeController> _log;

    public UsersMeController(
        UmServiceClient umProfile,
        IUsersStore users,
        IMemoryCache cache,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IUserManagementDualRoleClient dualRole,
        ITokenService tokens,
        ILogger<UsersMeController> log)
    {
        _umProfile = umProfile;
        _users = users;
        _cache = cache;
        _flags = flags;
        _dualRole = dualRole;
        _tokens = tokens;
        _log = log;
    }

    // -----------------------------------------------------------------
    // F-B — GET /v1/users/me
    // -----------------------------------------------------------------

    [HttpGet]
    // ADR-005 L2 §B self / any-authenticated {client, jeeber, admin}. L1 [Authorize] (class-level,
    // ADR-004) is preserved; this adds the L2 self-profile capability.
    [RequireCapability(Caps.ProfileReadSelf)]
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

        // active_role comes from the validated session token (the singular CURRENT role).
        // G3 mitigation: at most ProfileCacheSeconds stale after a switch (the switch
        // invalidates the cache; full access-token denylist deferred).
        var opaqueActive = HttpContext.User?.FindFirst("active_role")?.Value;
        var contractActive = JeebRoleTranslator.ToContract(opaqueActive);

        // H-B5 — available_roles MUST be the user's FULL role set, not just the active
        // role the token currently carries. A UM token re-issued by a role/switch embeds
        // only the now-active role in its "roles" claim, so reading roles straight off the
        // token would project ["client"] for a [client,jeeber] user. Resolve the full set
        // from the local UM projection (same source the switch path uses), falling back to
        // the token claim only when the projection is empty. THIN: no new logic/state — the
        // user's role membership is owned by user-management; we read + translate it.
        var opaqueRoles = await ResolveAvailableRolesAsync(userId, ct);
        var contractRoles = JeebRoleTranslator.ToContract(opaqueRoles);

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
    // F-A — POST /v1/users/me/role/switch  (JEEBER-SPINE Defect 2)
    // -----------------------------------------------------------------

    /// <summary>
    /// POST /v1/users/me/role/switch — switch the CURRENT (active) role of the caller's
    /// dual-role account. Body: <c>{ "role": "client" | "jeeber" }</c> (frozen Jeeb contract
    /// vocabulary). The gateway is a thin BFF: it validates the inbound contract role
    /// (<c>invalid_role</c> 400 BEFORE any UM call — N6), translates it to the OPAQUE role
    /// user-management understands, asks UM to PERSIST the active_role + RE-ISSUE the token
    /// pair (UM is the token authority on this path — N11 split-signer / CP-C), updates the
    /// local projection so the next gateway read reflects the switch, invalidates the /me
    /// profile cache, and relays UM's tokens verbatim. A UM 403 (the user does not hold the
    /// requested role — e.g. not yet KYC-approved as jeeber) maps straight to 403 (N5).
    ///
    /// <para>Re-introduces the route the mobile <c>DioRoleSwitchRepository</c> calls
    /// (<c>POST /v1/users/me/role/switch</c>): the ADR-004 "upgrade-not-switch" removal left
    /// the route absent, so the in-app driver switch hit 404. The KYC grant path
    /// (<see cref="IUserManagementDualRoleClient.AppendAvailableRoleAsync"/>) still owns
    /// granting the jeeber role; this action just flips which granted role is active.</para>
    /// </summary>
    [HttpPost("role/switch")]
    [RequireCapability(Caps.ProfileReadSelf)]
    [ProducesResponseType(typeof(RoleSwitchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> SwitchRole([FromBody] RoleSwitchRequestDto? body, CancellationToken ct)
    {
        if (!_flags.CurrentValue.UserManagement)
            return UpstreamDisabled();

        // I4 — identity ALWAYS from the bearer, never the body.
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        // N6 — validate the inbound Jeeb contract role and translate to OPAQUE BEFORE any UM
        // call. Anything outside {client, jeeber} is invalid_role 400 (no upstream dialed).
        var opaque = JeebRoleTranslator.ToOpaque(body?.Role);
        if (opaque is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_role", "Invalid role",
                $"Role '{body?.Role}' is not a recognised Jeeb role. Allowed: client, jeeber.");
        }

        try
        {
            // UM persists the active_role and re-issues a token PAIR; the gateway signs nothing.
            // DEFECT-1 FIX (iter5): UM's re-issued token carries iss/aud=user-management, but every
            // /v1/* route is [Authorize]'d to the GatewayBearerScheme (aud=jeeb-clients). Relaying
            // UM's token verbatim (as the prior `AccessToken = result.AccessToken` did) handed the
            // caller a token that 401s on the NEXT /v1/* call — a role switch broke the live session.
            // ADR-cleanest fix (ADR-004 "single session token carries the full role set"): return NO
            // replacement token. The active_role is ALREADY persisted by UM + projected locally
            // below, and the caller's existing aud=jeeb-clients session stays valid across the switch
            // (a stale active_role claim self-corrects on the next gateway-minted token via refresh /
            // re-login; available_roles/active_role in THIS body are authoritative immediately).
            var result = await _dualRole.RoleSwitchAsync(userId, opaque, ct);

            // Project the switch locally so the next gateway-minted/read path reflects it, and
            // invalidate the 30s /me profile cache so GET /v1/users/me is not stale (G3).
            // TokenService.IssueAsync reads active_role from THIS store, so the switch MUST be
            // persisted locally before the re-mint below for the new JWT to carry the new role.
            await _users.SwitchRoleAsync(userId, result.ActiveRole, ct);
            _cache.Remove(ProfileCacheKey(userId));

            // Resolve the user's FULL available-role set for the response body (the re-issued
            // UM token carries only the now-active role; available_roles must be the full set).
            var opaqueAvailable = await ResolveAvailableRolesAsync(userId, ct);
            var contractAvailable = JeebRoleTranslator.ToContract(opaqueAvailable);
            if (contractAvailable.Length == 0)
                contractAvailable = new[] { JeebRoleTranslator.ContractClient };

            var contractActive = JeebRoleTranslator.ToContract(result.ActiveRole);
            if (string.IsNullOrWhiteSpace(contractActive))
                contractActive = JeebRoleTranslator.ContractClient;

            // iter5 BATCHED-FIX B14 — re-issue a REAL gateway SESSION token whose claims reflect
            // the switch (aud=jeeb-clients, sub=userId, roles=full set, active_role=the now-active
            // role read from the store we just updated). The prior fix returned EMPTY tokens so the
            // caller kept its old session, but that left the active_role claim stale until the next
            // login — and a mobile build that DOES adopt this token would be handed an empty string
            // and break. Minting a fresh gateway token here gives the app a usable session that
            // immediately carries the new active_role, while NOT weakening auth (we still sign with
            // the gateway key, the UM aud=user-management token is never relayed). Best-effort: if
            // the mint faults we degrade to empty tokens (old session stays valid) rather than 500.
            var accessToken = string.Empty;
            var refreshToken = string.Empty;
            try
            {
                var pair = await _tokens.IssueAsync(userId, opaqueAvailable, ct);
                accessToken = pair.AccessToken;
                refreshToken = pair.RefreshToken;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "v1/users/me/role/switch re-mint failed for {UserId}; returning empty tokens so the caller keeps its existing session.",
                    userId);
            }

            return Ok(new RoleSwitchResponseDto
            {
                UserId = result.UserId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ActiveRole = contractActive,
                AvailableRoles = contractAvailable,
                User = new RoleSwitchUserBlock
                {
                    UserId = result.UserId,
                    ActiveRole = contractActive,
                    AvailableRoles = contractAvailable,
                },
            });
        }
        catch (UserManagementRoleNotAvailableException)
        {
            // N5 / ALT-1 — UM's role_not_available 403 (the user does not hold the requested
            // role, e.g. not KYC-approved as jeeber). The mobile client maps 403 → kycGated.
            return Problem(StatusCodes.Status403Forbidden, "role_not_available", "Role not available",
                $"You do not currently hold the '{body!.Role}' role. Complete the required onboarding first.");
        }
        catch (UserManagementCallException ex)
        {
            _log.LogWarning("v1/users/me/role/switch UM call failed (status {Status})", ex.StatusCode);
            return Problem(StatusCodes.Status502BadGateway, "upstream_fault", "Role switch upstream failure",
                "The user-management service returned an unexpected status while switching the active role.");
        }
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// Available roles for the response: the user's FULL persisted role set.
    ///
    /// <para>REALAPP fix — the AUTHORITATIVE source is user-management's
    /// <c>GET /api/User/{userId}/roles</c>
    /// (<see cref="IUserManagementDualRoleClient.GetUserRolesAsync"/>), which returns
    /// the user's complete OPAQUE <c>available_roles</c> set (e.g.
    /// <c>{customer,driver}</c>). The former order — local <see cref="IUsersStore"/>
    /// projection first — under-reported a dual-role user as only <c>["client"]</c>
    /// when the local projection lagged the UM row (a role-switch re-issues a token
    /// carrying only the now-active role, so the projection/claims are NOT the full
    /// set), and the mobile in-app role-switch was therefore never offered. THIN /
    /// ADR-0001 preserved: the gateway only READS + TRANSLATES the role set UM owns;
    /// it invents nothing.</para>
    ///
    /// <para>Fallback chain (each step used only when the prior yields nothing, so a
    /// UM blip never hard-breaks the read): authoritative UM roles -> local
    /// projection -> the validated session claims.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAvailableRolesAsync(string userId, CancellationToken ct)
    {
        // 1) AUTHORITATIVE — the persisted role set user-management owns.
        try
        {
            var um = await _dualRole.GetUserRolesAsync(userId, ct);
            if (um is { AvailableRoles.Count: > 0 }) return um.AvailableRoles;
        }
        catch (Exception ex)
        {
            // A UM roles-read blip is non-fatal: fall through to the local projection /
            // session claims rather than failing the whole /me read.
            _log.LogWarning(ex, "v1/users/me UM roles read failed; falling back to local projection/claims");
        }

        // 2) Local UM projection (the source the OTP-mint / role-switch paths upsert).
        var profile = await _users.GetByIdAsync(userId, ct);
        if (profile is { Roles.Count: > 0 }) return profile.Roles;

        // 3) Last resort — the roles claim on the validated session token.
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
