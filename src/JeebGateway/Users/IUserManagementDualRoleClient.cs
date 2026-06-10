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
    /// canonical user id. Idempotent: a repeated call for the same phone returns the same
    /// id with <c>isNew = false</c>.
    ///
    /// <para>JEB-1480 (GR2): the shared UM phone-identity endpoint is IDENTITY-ONLY and
    /// no longer emits roles. The DEFAULT role and all role/claim shaping are applied
    /// HERE in the gateway — the returned <see cref="PhoneFindOrCreateResult"/> carries
    /// the gateway's default opaque role decoration, NOT a UM-supplied claim.</para>
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

    /// <summary>
    /// S03 / ADR-0004 (H8). APPEND <paramref name="opaqueRole"/> to
    /// <paramref name="userId"/>'s <c>available_roles</c> jsonb in user-management
    /// (<c>POST /api/User/role/grant</c>), SET-semantics: a role the user already
    /// holds is a no-op (no duplicate). This is the identity-mutating side of the
    /// KYC approve transition — kyc-service decides the grant; the GATEWAY composes
    /// it here (ARCH LAW: kyc-service never calls UM). Returns the user's
    /// available roles after the append, and whether the role was newly added.
    /// </summary>
    /// <exception cref="UserManagementCallException">Any non-success status.</exception>
    Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct);
}

/// <summary>
/// Result of <see cref="IUserManagementDualRoleClient.AppendAvailableRoleAsync"/>.
/// <see cref="Added"/> is false when the user already held the role (set-semantics
/// no-op), so the gateway can report idempotent re-approval without a duplicate grant.
/// </summary>
public sealed record RoleGrantResult(
    string UserId,
    IReadOnlyList<string> AvailableRoles,
    bool Added);

/// <summary>
/// Result of <see cref="IUserManagementDualRoleClient.PhoneFindOrCreateAsync"/>. The id is
/// UM-canonical; role decoration is GATEWAY-OWNED (JEB-1480/JEB-1487 / GR2) — UM returns
/// IDENTITY ONLY. <see cref="PhoneHashRef"/> is the opaque HMAC-SHA256 lookup key emitted
/// by UM (non-reversible); callers may store it for audit/dedup without holding a raw phone.
/// OPAQUE role strings ({customer,driver}).
/// </summary>
public sealed record PhoneFindOrCreateResult(
    string UserId,
    bool IsNew,
    string PhoneHashRef,
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
