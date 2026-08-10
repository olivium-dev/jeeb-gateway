using JeebGateway.JeebWallet;

namespace JeebGateway.Users;

/// <summary>
/// Guards the shared opaque-role grant seam: a <c>driver</c> grant is forwarded only after the
/// same subject has a complete wallet inventory. Provisioning is ordered first because an orphan
/// zero-balance holder is safe, while a driver role without a settlement wallet is not.
/// </summary>
public sealed class WalletProvisioningDualRoleClient : IUserManagementDualRoleClient
{
    private readonly IUserManagementDualRoleClient _inner;
    private readonly IJeeberWalletProvisioner _wallet;
    private readonly ILogger<WalletProvisioningDualRoleClient> _log;

    public WalletProvisioningDualRoleClient(
        IUserManagementDualRoleClient inner,
        IJeeberWalletProvisioner wallet,
        ILogger<WalletProvisioningDualRoleClient> log)
    {
        _inner = inner;
        _wallet = wallet;
        _log = log;
    }

    public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct) =>
        _inner.PhoneFindOrCreateAsync(phone, ct);

    public Task<RoleSwitchReissueResult> RoleSwitchAsync(
        string userId,
        string opaqueRole,
        CancellationToken ct) =>
        _inner.RoleSwitchAsync(userId, opaqueRole, ct);

    public async Task<RoleGrantResult> AppendAvailableRoleAsync(
        string userId,
        string opaqueRole,
        CancellationToken ct)
    {
        if (string.Equals(opaqueRole, Roles.Jeeber, StringComparison.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(userId, out var holderId) || holderId == Guid.Empty)
            {
                _log.LogWarning(
                    "Jeeber role grant rejected because subject userId={UserId} is not a non-system UUID",
                    userId);
                throw new UserManagementCallException("wallet/holder/ensure", StatusCodes.Status502BadGateway);
            }

            try
            {
                await _wallet.EnsureAsync(holderId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Jeeber role grant deferred because wallet provisioning failed for userId={UserId}",
                    userId);
                throw new UserManagementCallException("wallet/holder/ensure", StatusCodes.Status502BadGateway);
            }
        }

        return await _inner.AppendAvailableRoleAsync(userId, opaqueRole, ct);
    }

    public Task<RoleGrantResult> RemoveAvailableRoleAsync(
        string userId,
        string opaqueRole,
        CancellationToken ct) =>
        _inner.RemoveAvailableRoleAsync(userId, opaqueRole, ct);

    public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct) =>
        _inner.GetUserRolesAsync(userId, ct);
}
