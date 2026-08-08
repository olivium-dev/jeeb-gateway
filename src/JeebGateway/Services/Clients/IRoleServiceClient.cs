namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed seam over olivium-dev/role-service (PR #1). Single tenant (app_id="jeeb");
/// role keys pass through verbatim, no translation layer.
/// </summary>
public interface IRoleServiceClient
{
    /// <summary>
    /// GET /v1/roles/apps/{appId}/subjects/{subjectId} — get-or-create read. An
    /// unknown subject returns an empty-roles result, never a 404/null.
    /// </summary>
    Task<RoleServiceSubjectRoles> GetOrCreateAsync(string appId, string subjectId, CancellationToken ct);

    /// <summary>
    /// POST .../grant. Idempotent (get-or-create at the DB layer): a role the
    /// subject already holds active 200s as a no-op instead of 201.
    /// </summary>
    Task<RoleServiceGrantResult> GrantAsync(
        string appId, string subjectId, string roleKey, string grantedBy,
        string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// POST .../revoke. Reached only via RoleServiceBackedDualRoleClient with the
    /// RoleService flag on (F3 unregister-as-jeeber); dark while the flag is off.
    /// </summary>
    Task<RoleServiceRevokeResult> RevokeAsync(
        string appId, string subjectId, string roleKey, string revokedBy,
        string? reassignActiveRoleTo, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// POST .../active-role. Requires the role to already be a held grant
    /// (trg_active_role_is_held) — callers must grant before setting active.
    /// </summary>
    Task<RoleServiceActiveRoleResult> SetActiveRoleAsync(
        string appId, string subjectId, string roleKey, string setBy,
        string idempotencyKey, CancellationToken ct);

    /// <summary>GET .../subjects:query — bounded list of subjects holding a role.</summary>
    Task<RoleServiceSubjectPage> ListByRoleAsync(
        string appId, string roleKey, string? status, string? cursor, int? limit, CancellationToken ct);
}

public sealed record RoleServiceRoleGrant(string RoleKey, string? Label, IReadOnlyList<string>? Scopes, string? GrantedBy, DateTimeOffset? GrantedAt);

public sealed record RoleServiceActiveRole(string RoleKey, string? SetBy, DateTimeOffset? SetAt);

public sealed record RoleServiceSubjectRoles(
    string AppId, string SubjectId, IReadOnlyList<RoleServiceRoleGrant> Roles, RoleServiceActiveRole? ActiveRole);

/// <summary><see cref="Created"/> distinguishes the 201 (new grant) from the 200 (already-active no-op).</summary>
public sealed record RoleServiceGrantResult(bool Created, RoleServiceSubjectRoles Subject);

public sealed record RoleServiceRevokeResult(RoleServiceSubjectRoles Subject);

public sealed record RoleServiceActiveRoleResult(RoleServiceSubjectRoles Subject);

public sealed record RoleServiceSubjectListItem(string SubjectId, string RoleKey, DateTimeOffset? GrantedAt);

public sealed record RoleServiceSubjectPage(IReadOnlyList<RoleServiceSubjectListItem> Items, string? NextCursor);

/// <summary>Status + the documented error code (e.g. <c>role_not_in_catalog</c>), when present.</summary>
public sealed class RoleServiceCallException : Exception
{
    public int StatusCode { get; }
    public string? ErrorCode { get; }

    public RoleServiceCallException(string operation, int statusCode, string? errorCode)
        : base($"role-service '{operation}' returned {statusCode}" + (errorCode is null ? "." : $" ({errorCode})."))
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }
}
