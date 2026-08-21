using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

/// <summary>
/// Decorates IUserManagementDualRoleClient: flag off forwards to inner unchanged;
/// flag on routes role membership and active-role authority to role-service. Identity stays on UM.
/// Every role-service path also reconciles the permanent client role: becoming a Jeeber is an
/// additive grant, never a replacement for the user's base client role.
/// </summary>
public sealed class RoleServiceBackedDualRoleClient : IUserManagementDualRoleClient
{
    private const string AppId = "jeeb";

    private readonly IUserManagementDualRoleClient _inner;
    private readonly IRoleServiceClient _roleService;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly ILogger<RoleServiceBackedDualRoleClient> _log;

    public RoleServiceBackedDualRoleClient(
        IUserManagementDualRoleClient inner,
        IRoleServiceClient roleService,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        ILogger<RoleServiceBackedDualRoleClient> log)
    {
        _inner = inner;
        _roleService = roleService;
        _flags = flags;
        _log = log;
    }

    public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct) =>
        _inner.PhoneFindOrCreateAsync(phone, ct);

    public async Task<RoleSwitchReissueResult> RoleSwitchAsync(
        string userId, string opaqueRole, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.RoleSwitchAsync(userId, opaqueRole, ct);
        }

        try
        {
            var before = await EnsureBaseClientRoleAsync(userId, ct);
            if (!HasRole(before, opaqueRole))
            {
                throw new UserManagementRoleNotAvailableException(userId, opaqueRole);
            }

            var idempotencyKey = $"role-switch:{userId}:{opaqueRole}:{Guid.NewGuid():n}";
            var result = await _roleService.SetActiveRoleAsync(
                AppId, userId, opaqueRole, "jeeb-gateway", idempotencyKey, ct);

            // The controller deliberately ignores upstream tokens and mints a gateway-audience
            // session after updating its local projection. Role Service owns only the active role.
            return new RoleSwitchReissueResult(
                userId,
                string.Empty,
                string.Empty,
                result.Subject.ActiveRole?.RoleKey ?? opaqueRole);
        }
        catch (UserManagementRoleNotAvailableException)
        {
            throw;
        }
        catch (RoleServiceCallException ex) when (ex.StatusCode is 403 or 409)
        {
            // The subject lost the requested grant between our read and active-role write.
            // Preserve the public switch contract: a valid-but-unheld role is a 403, not a 502.
            throw new UserManagementRoleNotAvailableException(userId, opaqueRole);
        }
        catch (RoleServiceCallException ex)
        {
            _log.LogWarning(ex,
                "role-service active-role failed for userId={UserId} role={Role} (status {Status})",
                userId, opaqueRole, ex.StatusCode);
            throw new UserManagementCallException("role-service/active-role", ex.StatusCode);
        }
    }

    public async Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.AppendAvailableRoleAsync(userId, opaqueRole, ct);
        }

        try
        {
            // KYC adds Jeeber to an existing regular user. Reconcile the permanent base role
            // before the requested grant so a partial/no-backfill Role Service record heals to
            // {customer, driver}, rather than replacing customer with driver.
            if (!string.Equals(opaqueRole, Roles.Client, StringComparison.OrdinalIgnoreCase))
            {
                await EnsureBaseClientRoleAsync(userId, ct);
            }

            // ARCH LAW: gateway composes the grant on kyc-service's behalf. Fresh key
            // per call is safe: grant is ALSO get-or-create at the DB layer.
            var idempotencyKey = $"kyc-grant:{userId}:{opaqueRole}:{Guid.NewGuid():n}";
            var result = await _roleService.GrantAsync(AppId, userId, opaqueRole, "kyc-service", idempotencyKey, ct);
            var roles = result.Subject.Roles.Select(r => r.RoleKey).ToArray();
            return new RoleGrantResult(userId, roles, result.Created);
        }
        catch (RoleServiceCallException ex)
        {
            // Preserve the UM-path contract: callers catch UserManagementCallException.
            _log.LogWarning(ex,
                "role-service grant failed for userId={UserId} role={Role} (status {Status})",
                userId, opaqueRole, ex.StatusCode);
            throw new UserManagementCallException("role-service/grant", ex.StatusCode);
        }
    }

    public async Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.RemoveAvailableRoleAsync(userId, opaqueRole, ct);
        }

        try
        {
            // The client role is the account's permanent base role, not an optional persona.
            // No current controller requests this, but keeping the invariant at the authority
            // adapter prevents a future caller from turning a dual-role user into a role-less one.
            if (string.Equals(opaqueRole, Roles.Client, StringComparison.OrdinalIgnoreCase))
            {
                var unchanged = await EnsureBaseClientRoleAsync(userId, ct);
                return new RoleGrantResult(
                    userId,
                    unchanged.Roles.Select(r => r.RoleKey).ToArray(),
                    false);
            }

            // reassign_active_role_to is ALWAYS validated: role-service 409s
            // (role.active_role_not_held) unless the target is a role already held.
            var before = await EnsureBaseClientRoleAsync(userId, ct);
            var reassignTo = PickReassignTarget(before, opaqueRole);

            // Fresh key per call: an already-revoked role 200s with already_revoked.
            var idempotencyKey = $"self-unregister:{userId}:{opaqueRole}:{Guid.NewGuid():n}";
            var result = await _roleService.RevokeAsync(
                AppId, userId, opaqueRole, "self-unregister", reassignTo, idempotencyKey, ct);

            var roles = result.Subject.Roles.Select(r => r.RoleKey).ToArray();
            var removed = !roles.Contains(opaqueRole, StringComparer.OrdinalIgnoreCase);
            return new RoleGrantResult(userId, roles, removed);
        }
        catch (RoleServiceCallException ex)
        {
            // Same contract preservation as the grant path: callers catch UserManagementCallException.
            _log.LogWarning(ex,
                "role-service revoke failed for userId={UserId} role={Role} (status {Status})",
                userId, opaqueRole, ex.StatusCode);
            throw new UserManagementCallException("role-service/revoke", ex.StatusCode);
        }
    }

    /// <summary>
    /// Active-role successor for a revoke: Client when still held, else any other held
    /// role, else null (valid whenever the revoked role is not the active one).
    /// </summary>
    private static string? PickReassignTarget(RoleServiceSubjectRoles subject, string revokedRole)
    {
        var remaining = subject.Roles
            .Select(r => r.RoleKey)
            .Where(k => !string.Equals(k, revokedRole, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return remaining.FirstOrDefault(k => string.Equals(k, Roles.Client, StringComparison.OrdinalIgnoreCase))
            ?? remaining.FirstOrDefault();
    }

    public async Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.GetUserRolesAsync(userId, ct);
        }

        try
        {
            // Self-heals records created while the Role Service cutover was only partially
            // backfilled (the production failure mode was roles=[driver], no customer).
            var subject = await EnsureBaseClientRoleAsync(userId, ct);
            var roles = subject.Roles.Select(r => r.RoleKey).ToArray();
            return new UserRolesResult(subject.SubjectId, roles, subject.ActiveRole?.RoleKey);
        }
        catch (Exception ex)
        {
            // Safe-degrade like the UM client: a blip never hard-fails login.
            _log.LogWarning(ex, "role-service get-roles failed for userId={UserId}", userId);
            return null;
        }
    }

    private async Task<RoleServiceSubjectRoles> EnsureBaseClientRoleAsync(
        string userId, CancellationToken ct)
    {
        var subject = await _roleService.GetOrCreateAsync(AppId, userId, ct);
        if (HasRole(subject, Roles.Client))
        {
            return subject;
        }

        // A fresh operation key is intentional. The read above suppresses normal duplicates,
        // while a fresh key lets reconciliation re-grant customer if an out-of-band mutation
        // ever revoked it after an earlier idempotent grant was recorded.
        var idempotencyKey = $"base-role:v1:{userId}:{Roles.Client}:{Guid.NewGuid():n}";
        var result = await _roleService.GrantAsync(
            AppId, userId, Roles.Client, "jeeb-gateway", idempotencyKey, ct);

        _log.LogInformation(
            "Reconciled permanent client role in role-service for userId={UserId}", userId);
        return result.Subject;
    }

    private static bool HasRole(RoleServiceSubjectRoles subject, string role) =>
        subject.Roles.Any(r => string.Equals(r.RoleKey, role, StringComparison.OrdinalIgnoreCase));
}
