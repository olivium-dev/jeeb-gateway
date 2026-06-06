namespace JeebGateway.Users;

/// <summary>
/// S02 Wave-1 (ADR-003) typed seam over the two NEW user-management endpoints the
/// gateway thin-BFF orchestrates:
/// <list type="bullet">
///   <item><description><c>POST /api/users/phone-identity/find-or-create</c> (JEB-1422) — the
///     phone-keyed identity used by the OTP verify path (F-C).</description></item>
///   <item><description><c>POST /api/User/role/switch</c> (JEB-39) — UM is the token authority:
///     it persists the OPAQUE active_role AND re-issues the access+refresh pair. The
///     gateway forwards the result verbatim and signs NOTHING (CP-C / N11).</description></item>
/// </list>
///
/// <para><b>Why a hand-authored seam and not the NSwag client.</b> These two routes are
/// net-new on the un-deployed UM keystone branch (<c>feature/jeeb-mvp-dual-role-identity</c>),
/// so the committed generated <c>ServiceUserManagementClient</c> does not yet expose them and
/// the live UM OpenAPI cannot be re-scraped until UM deploys (build order STEP 1 -&gt; STEP 2).
/// This is the org-blessed "extend a generated client" pattern: a thin adapter over the SAME
/// named <c>HttpClient</c> base address, replaced by a regenerated NSwag client once UM ships.
/// All vocabulary on this seam is OPAQUE (<c>customer</c>/<c>driver</c>); the
/// <see cref="JeebRoleTranslator"/> is the only place the Jeeb contract vocab appears.</para>
/// </summary>
public interface IUserManagementDualRoleClient
{
    /// <summary>
    /// Find-or-create the UM identity for <paramref name="phone"/> (E.164). Returns the
    /// canonical user id plus the user's OPAQUE roles. Idempotent: a repeated call for the
    /// same phone returns the same id with <c>isNew = false</c>.
    /// </summary>
    /// <exception cref="UserManagementCallException">The upstream returned a non-success status.</exception>
    Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct);

    /// <summary>
    /// Ask UM to switch <paramref name="userId"/> to the OPAQUE <paramref name="opaqueRole"/>
    /// and RE-ISSUE the token pair. UM signs; the gateway relays the returned tokens verbatim.
    /// </summary>
    /// <exception cref="UserManagementRoleNotAvailableException">
    /// UM signalled 403 role_not_available (the user does not hold the requested role) — the
    /// gateway maps this straight through to 403 (N5 / ALT-1).</exception>
    /// <exception cref="UserManagementCallException">Any other non-success status.</exception>
    Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct);
}

/// <summary>Result of <see cref="IUserManagementDualRoleClient.PhoneFindOrCreateAsync"/> — OPAQUE roles.</summary>
public sealed record PhoneFindOrCreateResult(
    string UserId,
    bool IsNew,
    IReadOnlyList<string> AvailableRoles,
    string ActiveRole);

/// <summary>
/// Result of <see cref="IUserManagementDualRoleClient.RoleSwitchAsync"/>. <see cref="AccessToken"/>
/// and <see cref="RefreshToken"/> are UM-issued; the gateway returns them WITHOUT re-signing.
/// <see cref="ActiveRole"/> is the new OPAQUE active role UM persisted.
/// </summary>
public sealed record RoleSwitchReissueResult(
    string UserId,
    string AccessToken,
    string RefreshToken,
    string ActiveRole);

/// <summary>A non-success response from a user-management dual-role call (other than 403 role_not_available).</summary>
public sealed class UserManagementCallException : Exception
{
    public int StatusCode { get; }

    public UserManagementCallException(string operation, int statusCode)
        : base($"user-management '{operation}' returned status {statusCode}.")
    {
        StatusCode = statusCode;
    }
}

/// <summary>
/// UM signalled 403 role_not_available on a role switch: the user does not hold the requested
/// role. Distinct from the gateway-local 400 <c>invalid_role</c> (N5 vs N6). Mapped straight to 403.
/// </summary>
public sealed class UserManagementRoleNotAvailableException : Exception
{
    public UserManagementRoleNotAvailableException(string userId, string opaqueRole)
        : base($"user-management rejected role '{opaqueRole}' for user '{userId}' (role_not_available).")
    {
    }
}
