using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

/// <summary>
/// Decorates IUserManagementDualRoleClient: flag off forwards to inner unchanged;
/// flag on routes grant/revoke/read to role-service. Identity + role-switch stay on UM always.
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

    public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct) =>
        _inner.RoleSwitchAsync(userId, opaqueRole, ct);

    public async Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.AppendAvailableRoleAsync(userId, opaqueRole, ct);
        }

        try
        {
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
            // Fresh key per call: revoke is idempotent at the DB layer. Active role is
            // reassigned to Client, matching the gateway's own local mirror.
            var idempotencyKey = $"self-unregister:{userId}:{opaqueRole}:{Guid.NewGuid():n}";
            var result = await _roleService.RevokeAsync(
                AppId, userId, opaqueRole, "self-unregister", Roles.Client, idempotencyKey, ct);

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

    public async Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
    {
        if (!_flags.CurrentValue.RoleService)
        {
            return await _inner.GetUserRolesAsync(userId, ct);
        }

        try
        {
            var subject = await _roleService.GetOrCreateAsync(AppId, userId, ct);
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
}
