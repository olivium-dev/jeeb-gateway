using JeebGateway.Auth.Capabilities;
using JeebGateway.Availability;
using JeebGateway.JeebWallet;
using JeebGateway.Requests;
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
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;
using GetHolderWallets = JeebGateway.service.ServiceWallet.GetHolderWallets;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// S02 dual-role BFF read surface. THIN: identity and persistence live in user-management;
/// this controller orchestrates the read and translates vocabulary (opaque {customer,driver}
/// &lt;-&gt; Jeeb contract {client,jeeber}).
///
/// Jeeber membership is additive: every account remains a client after S03 KYC approval adds
/// Jeeber. <c>active_role</c> selects the current UI/persona without removing either membership,
/// and the gateway-minted session (aud=jeeb-clients) carries the FULL role set. The
/// <c>POST /v1/users/me/role/switch</c> action persists only that active-role selection.
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
// NOTE (JEBV4-261): intentionally NO class-level [Produces(...)]. A [Produces] filter
// CLEARS an ObjectResult's own ContentTypes and forces the FIRST listed media type,
// which downgraded the RFC 7807 error bodies emitted by OtpSignInProblems.UsersProblem
// (ObjectResult.ContentTypes = "application/problem+json") to "application/json".
// Omitting it lets each result carry its correct media type — success → application/json,
// error → application/problem+json — while the per-action [ProducesResponseType] still
// documents the shapes for Swagger. Mirrors the AuthRefreshV1Controller fix (PR #242).
public sealed class UsersMeController : ControllerBase
{
    private const int ProfileCacheSeconds = 30;

    private readonly UmServiceClient _umProfile;
    private readonly IUsersStore _users;
    private readonly IMemoryCache _cache;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly IUserManagementDualRoleClient _dualRole;
    private readonly IDevSeededRoleStore _seededRoles;
    private readonly ITokenService _tokens;
    private readonly IRequestsStore _requests;
    private readonly ServiceWalletClient _wallet;
    private readonly IJeeberForceOfflineOnUnregister _forceOffline;
    private readonly IPendingOffersStore _pendingOffers;
    private readonly IOptions<GatewayPublicOptions> _publicOptions;
    private readonly ILogger<UsersMeController> _log;

    public UsersMeController(
        UmServiceClient umProfile,
        IUsersStore users,
        IMemoryCache cache,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        IUserManagementDualRoleClient dualRole,
        IDevSeededRoleStore seededRoles,
        ITokenService tokens,
        IRequestsStore requests,
        ServiceWalletClient wallet,
        IJeeberForceOfflineOnUnregister forceOffline,
        IPendingOffersStore pendingOffers,
        IOptions<GatewayPublicOptions> publicOptions,
        ILogger<UsersMeController> log)
    {
        _umProfile = umProfile;
        _users = users;
        _cache = cache;
        _flags = flags;
        _dualRole = dualRole;
        _seededRoles = seededRoles;
        _tokens = tokens;
        _requests = requests;
        _wallet = wallet;
        _forceOffline = forceOffline;
        _pendingOffers = pendingOffers;
        _publicOptions = publicOptions;
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

        var cacheKey = ProfileCacheKeys.ForUser(userId);
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
                // F5: ProfilePic is a bare CDN object ref — project it to a loadable URL.
                display = new ProfileDisplay(
                    profile?.Username,
                    profile?.Email,
                    AvatarUrlResolver.Absolutize(
                        profile?.ProfilePic, userId, _publicOptions.Value.PublicBaseUrl));

                // jeeberName gap fix: user-management's username is the ONLY display
                // name real (OTP-minted) accounts carry anywhere in the flow, and the
                // deliveries jeeberName enrichment reads the gateway's LOCAL users
                // projection — which the OTP mint fills with Name = "". Hydrate the
                // projection from this successful UM read so a jeeber who has a UM
                // username gets a resolvable display name after their first /me read
                // (the app calls this at login), without any extra UM round-trip.
                // Best-effort: a projection write fault never degrades the read.
                await HydrateLocalDisplayNameAsync(userId, display, ct);
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
    /// (<c>invalid_role</c> 400 BEFORE any authority call — N6), translates it to the OPAQUE role
    /// configured role authority understands, asks it to PERSIST the active_role, updates the
    /// local projection so the next gateway read reflects the switch, invalidates the /me
    /// profile cache, and mints a fresh gateway-audience session carrying the full additive role
    /// set. A role-authority rejection (the user does not hold the requested role — e.g. not yet
    /// KYC-approved as jeeber) maps straight to 403 (N5).
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

        // N6 — validate the inbound Jeeb contract role and translate to OPAQUE BEFORE any
        // authority call. Anything outside {client, jeeber} is invalid_role 400 (no upstream dialed).
        var opaque = JeebRoleTranslator.ToOpaque(body?.Role);
        if (opaque is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "invalid_role", "Invalid role",
                $"Role '{body?.Role}' is not a recognised Jeeb role. Allowed: client, jeeber.");
        }

        try
        {
            // The configured authority persists active_role. With Role Service enabled the
            // adapter never consults UM role state, avoiding split-brain after KYC grants.
            var result = await _dualRole.RoleSwitchAsync(userId, opaque, ct);

            // Project the switch locally so the next gateway-minted/read path reflects it, and
            // invalidate the 30s /me profile cache so GET /v1/users/me is not stale (G3).
            // TokenService.IssueAsync reads active_role from THIS store, so the switch MUST be
            // persisted locally before the re-mint below for the new JWT to carry the new role.
            await _users.SwitchRoleAsync(userId, result.ActiveRole, ct);
            _cache.Remove(ProfileCacheKeys.ForUser(userId));

            // Resolve the user's FULL additive role set for both the response and the new token.
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
            // immediately carries the new active_role, while NOT weakening auth (we sign with the
            // gateway key; no upstream token is relayed). Best-effort: if
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
            // N5 / ALT-1 — role authority says the user does not hold the requested
            // role (e.g. not KYC-approved as jeeber). The mobile client maps 403 → kycGated.
            return Problem(StatusCodes.Status403Forbidden, "role_not_available", "Role not available",
                $"You do not currently hold the '{body!.Role}' role. Complete the required onboarding first.");
        }
        catch (UserManagementCallException ex)
        {
            _log.LogWarning("v1/users/me/role/switch authority call failed (status {Status})", ex.StatusCode);
            return Problem(StatusCodes.Status502BadGateway, "upstream_fault", "Role switch upstream failure",
                "The role authority returned an unexpected status while switching the active role.");
        }
    }

    // -----------------------------------------------------------------
    // F3 — POST /v1/users/me/role/unregister (unregister-as-jeeber, NOT account deletion)
    // -----------------------------------------------------------------

    /// <summary>
    /// Self-only Jeeber-role removal (not account deletion — design §3). Guards mirror
    /// <see cref="DualRoleService"/>'s BR-1 shape; UM has no revoke op yet (correction 9),
    /// so this ships DARK behind 502 <c>upstream_fault</c> until UM adds one.
    /// </summary>
    [HttpPost("role/unregister")]
    // Write-adjacent self-mutation, matching account-deletion's capability choice
    // (UserController.cs:951), not ProfileReadSelf which SwitchRole uses.
    [RequireCapability(Caps.ProfileWriteSelf)]
    [ProducesResponseType(typeof(RoleSwitchResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> UnregisterAsJeeber(CancellationToken ct)
    {
        if (!_flags.CurrentValue.UserManagement)
            return UpstreamDisabled();

        // I4 — identity ALWAYS from the bearer, never body/query.
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauth))
            return unauth;

        // Must currently hold the jeeber role — fires before RevokeRoleAsync's own no-op
        // semantics are reachable, so a second call is 404, not a silent 200 (correction 10).
        var opaqueRoles = await ResolveAvailableRolesAsync(userId, ct);
        if (!opaqueRoles.Contains(Roles.Jeeber, StringComparer.OrdinalIgnoreCase))
        {
            return Problem(StatusCodes.Status404NotFound, "not_a_jeeber", "Not a jeeber",
                "This account does not currently hold the jeeber role.");
        }

        // Guard 1 — active jeeber deliveries, counted regardless of ActiveRole (unlike
        // ValidateRoleSwitchAsync, which only counts when ActiveRole==Jeeber).
        var activeDeliveries = await _requests.CountActiveForJeeberAsync(userId, ct);
        if (activeDeliveries > 0)
        {
            return Problem(StatusCodes.Status409Conflict, "active_delivery", "Active delivery in progress",
                $"Complete or hand off {activeDeliveries} active delivery(ies) as jeeber before unregistering.");
        }

        // Guard 2 — positive wallet/earnings balance.
        if (Guid.TryParse(userId, out var holderId))
        {
            GetHolderWallets? holder;
            try
            {
                holder = await _wallet.WalletsAsync(holderId, ct);
            }
            catch (WalletApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
            {
                holder = null; // no wallet provisioned yet — an honest zero balance.
            }
            catch (Exception ex)
            {
                // Money-adjacent guard fails CLOSED: an unreachable wallet-service must never
                // silently let a real positive balance through (mirrors F1's OQ1 posture).
                _log.LogWarning(ex,
                    "v1/users/me/role/unregister: wallet balance read failed for {UserId}.", userId);
                return Problem(StatusCodes.Status503ServiceUnavailable, "wallet_service_unavailable",
                    "Wallet balance could not be verified",
                    "The wallet balance check could not run; try again shortly.");
            }

            if (JeebWalletProjection.ProjectBalance(holder).AvailableBalance > 0)
            {
                return Problem(StatusCodes.Status409Conflict, "positive_wallet_balance", "Positive wallet balance",
                    "Settle or withdraw your wallet balance before unregistering as a jeeber.");
            }
        }
        else
        {
            // Permissive non-UUID ids (the OTP UM-down phone-keyed fallback) have no
            // wallet-service holder row by construction — nothing to guard against.
            _log.LogWarning(
                "v1/users/me/role/unregister: userId {UserId} is not a GUID; wallet guard skipped.", userId);
        }

        // Guard 3 — mandatory force-offline BEFORE the revoke (correction 6): matching reads
        // presence, not roles, so an online jeeber must stop being a candidate immediately.
        await _forceOffline.ForceOfflineAsync(userId, ct);

        // Best-effort withdraw of outstanding pre-accept offers — offer-service exposes no
        // bulk withdraw-for-jeeber route in production (JEBV4-148); never blocks the 200.
        try
        {
            await _pendingOffers.WithdrawForJeeberAsync(userId, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "v1/users/me/role/unregister: best-effort offer withdraw failed for {UserId}.", userId);
        }

        // Deliberately NOT touched: push tokens (account-scoped; clearing worsens the
        // zombie-token backlog), chat threads (survive), GPS position (self-expiring TTL).

        try
        {
            // UM remains the role authority (same split as the KYC-grant). 404s live today
            // (correction 9); the catch below turns that into a documented 502, never a fake success.
            var result = await _dualRole.RemoveAvailableRoleAsync(userId, Roles.Jeeber, ct);

            // Local mirror ONLY after UM succeeds (no partial apply). RevokeRoleAsync
            // durably mirrors into Postgres (correction 2) and flips ActiveRole off Jeeber.
            await _users.RevokeRoleAsync(userId, Roles.Jeeber, ct);
            _cache.Remove(ProfileCacheKeys.ForUser(userId));

            var contractAvailable = JeebRoleTranslator.ToContract(result.AvailableRoles);
            if (contractAvailable.Length == 0)
                contractAvailable = new[] { JeebRoleTranslator.ContractClient };

            var localProfile = await _users.GetByIdAsync(userId, ct);
            var contractActive = JeebRoleTranslator.ToContract(localProfile?.ActiveRole);
            if (string.IsNullOrWhiteSpace(contractActive))
                contractActive = JeebRoleTranslator.ContractClient;

            var accessToken = string.Empty;
            var refreshToken = string.Empty;
            try
            {
                var pair = await _tokens.IssueAsync(userId, result.AvailableRoles, ct);
                accessToken = pair.AccessToken;
                refreshToken = pair.RefreshToken;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "v1/users/me/role/unregister re-mint failed for {UserId}; returning empty tokens so the caller keeps its existing session.",
                    userId);
            }

            return Ok(new RoleSwitchResponseDto
            {
                UserId = userId,
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                ActiveRole = contractActive,
                AvailableRoles = contractAvailable,
                User = new RoleSwitchUserBlock
                {
                    UserId = userId,
                    ActiveRole = contractActive,
                    AvailableRoles = contractAvailable,
                },
            });
        }
        catch (UserManagementCallException ex)
        {
            _log.LogWarning("v1/users/me/role/unregister UM call failed (status {Status})", ex.StatusCode);
            return Problem(StatusCodes.Status502BadGateway, "upstream_fault", "Unregister upstream failure",
                "The user-management service does not yet support removing the jeeber role.");
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
    ///
    /// <para>SELF-DRIFT FIX (JEBV4-314 companion) — after the base set is resolved, UNION any
    /// DEV-seeded roles so this read reports the SAME effective role set THIS gateway minted at
    /// login. <see cref="AuthEmailFacadeController.ResolveRolesAsync"/> unions the
    /// <see cref="IDevSeededRoleStore"/> into the JWT it mints (a seeded admin logs in carrying
    /// <c>roles:[customer,admin]</c>); without the same union here the /me read returns
    /// user-management's authoritative set ALONE — which never learned the seed (register has no
    /// role column), so a seeded admin resolved to <c>[customer] → [client]</c>, contradicting the
    /// mint and gating every admin CMS surface closed (the shell derives caps from
    /// <c>available_roles</c>). The store is ONLY ever populated by the <c>[DevOnly]</c>
    /// <c>POST /dev/seed/user</c> action, so in production (and every non-seeded request) it
    /// resolves to null and the union is a strict no-op — user-management stays authoritative for
    /// real identity, and a client-only identity still surfaces exactly <c>[client]</c>. Unioned at
    /// the OPAQUE level so <c>admin</c> passes through <see cref="JeebRoleTranslator.ToContract(string?)"/>
    /// unchanged (the vocabulary the CMS shell's <c>capabilitiesFromRoles</c> understands).</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAvailableRolesAsync(string userId, CancellationToken ct)
    {
        IReadOnlyList<string> baseRoles = Array.Empty<string>();

        // 1) AUTHORITATIVE — the persisted role set user-management owns.
        try
        {
            var um = await _dualRole.GetUserRolesAsync(userId, ct);
            if (um is { AvailableRoles.Count: > 0 }) baseRoles = um.AvailableRoles;
        }
        catch (Exception ex)
        {
            // A UM roles-read blip is non-fatal: fall through to the local projection /
            // session claims rather than failing the whole /me read.
            _log.LogWarning(ex, "v1/users/me UM roles read failed; falling back to local projection/claims");
        }

        // 2) Local UM projection (the source the OTP-mint / role-switch paths upsert).
        if (baseRoles.Count == 0)
        {
            var profile = await _users.GetByIdAsync(userId, ct);
            if (profile is { Roles.Count: > 0 }) baseRoles = profile.Roles;
        }

        // 3) Last resort — the roles claim on the validated session token.
        if (baseRoles.Count == 0)
            baseRoles = UserIdentity.GetRoles(HttpContext);

        // Union the dev-seeded roles so /me matches the login mint (see summary). Dev-only store:
        // null (a strict no-op) for every real user. Resolve by userId — the seed records both the
        // canonical userId and the login email; the userId is the join key the bearer's sub carries.
        var seeded = _seededRoles.Resolve(userId, email: null);
        if (seeded is { Count: > 0 })
            baseRoles = baseRoles.Union(seeded, StringComparer.OrdinalIgnoreCase).ToList();

        return baseRoles;
    }

    /// <summary>
    /// jeeberName gap fix — best-effort mirror of the UM display name into the local
    /// users projection (the store the deliveries jeeberName enrichment reads). Only
    /// fills a MISSING local name; a name already learned locally (e.g. via the
    /// profile-update mirror) is never overwritten by this passive read path. Never
    /// throws into the /me read.
    /// </summary>
    private async Task HydrateLocalDisplayNameAsync(string userId, ProfileDisplay display, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(display.Name)) return;

        try
        {
            var local = await _users.GetByIdAsync(userId, ct);
            if (!string.IsNullOrWhiteSpace(local?.Name)) return;

            await _users.UpdateProfileAsync(userId, new ProfilePatch { Name = display.Name.Trim() }, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "v1/users/me local display-name hydration failed for {UserId}; read is unaffected.", userId);
        }
    }

    private ObjectResult UpstreamDisabled() => Problem(
        StatusCodes.Status503ServiceUnavailable, "user_management_unavailable",
        "User-management not enabled",
        "The dual-role identity surface requires user-management orchestration "
        + "(FeatureFlags:UseUpstream:UserManagement is false).");

    private ObjectResult Problem(int status, string shortType, string title, string detail)
        => OtpSignInProblems.UsersProblem(this, status, shortType, title, detail);

    private sealed record ProfileDisplay(string? Name, string? Email, string? AvatarUrl);
}
