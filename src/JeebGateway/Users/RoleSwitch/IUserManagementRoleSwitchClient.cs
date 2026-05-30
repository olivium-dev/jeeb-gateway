namespace JeebGateway.Users.RoleSwitch;

/// <summary>
/// T-BE-003 / JEB-39 — gateway-side adapter contract for the role-switch
/// endpoint on <c>olivium-dev/user-management</c>:
///   <c>PATCH /api/User/{userId}/active-role</c>
///   body  { active_role: "client" | "jeeber" }
///   200   { userId, available_roles, active_role, active_role_changed_at }
///   400   ProblemDetails (invalid role)
///   403   ProblemDetails (role not in available_roles)
///   404   user not found
///
/// Production wiring replaces the in-memory default with the NSwag-generated
/// <c>UserManagementClient.RoleSwitchAsync</c> behind a thin adapter
/// implementing this interface — the same pattern used by
/// <see cref="JeebGateway.Auth.OtpSignIn.IUserManagementPhoneIdentityClient"/>
/// in the OTP sign-in path.
///
/// AC4 observability: the controller logs <c>role.switched</c> on every
/// success; AC5 perf: this call is on the hot path of the 300 ms budget,
/// so the production HTTP adapter is configured against the
/// <c>user-management</c> named HttpClient (Polly retry + circuit-breaker
/// + 10 s timeout per attempt — see <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>).
/// </summary>
public interface IUserManagementRoleSwitchClient
{
    /// <summary>
    /// Look up the user's dual-role identity. Returns null when no such
    /// user exists. Used to populate the <c>available_roles</c> claim list
    /// embedded in the freshly minted JWT after a role switch, and to
    /// produce the <c>user</c> block on the response so the mobile app
    /// can update its local profile without a second round-trip.
    /// </summary>
    Task<RoleSwitchUserSnapshot?> GetUserAsync(
        Guid userId,
        CancellationToken ct = default);

    /// <summary>
    /// Atomically validate-and-switch <paramref name="userId"/>'s active
    /// role. <see cref="RoleSwitchOutcome.RoleNotAvailable"/> when the
    /// requested role is not in the user's persisted
    /// <c>available_roles</c> (AC2 — 403). <see cref="RoleSwitchOutcome.UserNotFound"/>
    /// when the user does not exist (404). Same-role switches are
    /// idempotent and return <see cref="RoleSwitchOutcome.Ok"/>.
    /// </summary>
    Task<RoleSwitchResult> SwitchActiveRoleAsync(
        Guid userId,
        string newRole,
        CancellationToken ct = default);
}

/// <summary>
/// Outcome of a <see cref="IUserManagementRoleSwitchClient.SwitchActiveRoleAsync"/>
/// call. The controller maps these to HTTP status codes:
///   Ok               → 200
///   RoleNotAvailable → 403 ProblemDetails type=role_not_available
///   UserNotFound     → 404
/// (Invalid role values are rejected by the controller BEFORE this call —
/// 400 ProblemDetails type=invalid_role.)
/// </summary>
public enum RoleSwitchOutcome
{
    Ok,
    RoleNotAvailable,
    UserNotFound,
}

/// <summary>
/// Result wrapper for <see cref="IUserManagementRoleSwitchClient.SwitchActiveRoleAsync"/>.
/// On a successful switch <see cref="Snapshot"/> reflects the new active
/// role and <see cref="PreviousRole"/> carries the value that was active
/// just before the switch — needed for the AC4 audit log line.
/// </summary>
public sealed record RoleSwitchResult(
    RoleSwitchOutcome Outcome,
    RoleSwitchUserSnapshot? Snapshot,
    string? PreviousRole);

/// <summary>
/// Persisted dual-role identity as the gateway sees it after the switch.
/// Mirrors the user-management <c>UserRolesResponse</c> shape from
/// T-BE-002 (JEB-38) so adapters can map 1:1.
/// </summary>
public sealed record RoleSwitchUserSnapshot(
    Guid UserId,
    IReadOnlyList<string> AvailableRoles,
    string ActiveRole,
    DateTimeOffset? ActiveRoleChangedAt);

/// <summary>
/// Default registration used until the NSwag-generated UserManagementClient
/// is wired (TODO T-backend-bff-user in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>). Mirrors
/// <see cref="JeebGateway.Auth.OtpSignIn.NotConfiguredUserManagementPhoneIdentityClient"/>
/// from the OTP sign-in path — fails closed with a precise message rather
/// than silently switching roles against an unverified identity store.
/// </summary>
public sealed class NotConfiguredUserManagementRoleSwitchClient
    : IUserManagementRoleSwitchClient
{
    public Task<RoleSwitchUserSnapshot?> GetUserAsync(Guid userId, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "UserManagementApi:BaseUrl is not configured. The role-switch path " +
            "requires the user-management T-BE-002 endpoints " +
            "(GET /api/User/{id}/roles, PATCH /api/User/{id}/active-role). " +
            "Register an IUserManagementRoleSwitchClient implementation (typically " +
            "the NSwag-generated UserManagementClient wrapped in an adapter) before " +
            "this endpoint is reachable in production.");

    public Task<RoleSwitchResult> SwitchActiveRoleAsync(
        Guid userId, string newRole, CancellationToken ct = default)
        => throw new InvalidOperationException(
            "UserManagementApi:BaseUrl is not configured. See GetUserAsync for the " +
            "required production wiring.");
}
