using Microsoft.Extensions.Logging;

namespace JeebGateway.Users;

/// <summary>
/// KYC approval GRANTS the jeeber role (<see cref="IUserManagementDualRoleClient.AppendAvailableRoleAsync"/>)
/// but never chose which granted role is ACTIVE, so an approved jeeber kept
/// <c>active_role = customer</c> in user-management for good. That value is what the session
/// mint, <c>GET /v1/users/me</c> and the push topic map all read, so the app kept the jeeber on
/// the CLIENT surface and refused every <c>jeeb://jeeber/**</c> push deep link.
///
/// <para>Server-side counterpart of the mobile <c>JeeberRoleActivator</c>, which fires a
/// <c>POST /v1/users/me/role/switch</c> from the KYC-approved view. That compensation only runs
/// when the approved view happens to render; the grant is the authoritative moment, so the
/// gateway makes the active role follow it here.</para>
/// </summary>
public static class JeeberActiveRolePromotion
{
    /// <summary>
    /// Best-effort: makes <see cref="Roles.Jeeber"/> the persisted active role once the KYC grant
    /// confirms the user holds it. Returns true when user-management persisted the promotion.
    ///
    /// <para>Never throws and never rolls the approve back — the approve has already committed
    /// (N14), so a user-management blip only defers the promotion to the next grant or to the
    /// mobile activator. Switching to the already-active role is a documented UM 200 no-op, so
    /// re-approval is idempotent.</para>
    /// </summary>
    public static async Task<bool> PromoteAsync(
        IUserManagementDualRoleClient userManagement,
        string userId,
        string opaqueRole,
        RoleGrantResult grant,
        ILogger log,
        CancellationToken ct)
    {
        if (!string.Equals(opaqueRole, Roles.Jeeber, StringComparison.OrdinalIgnoreCase))
            return false;

        // Only promote a role the grant proves the user actually holds — never mint authority.
        if (!grant.AvailableRoles.Contains(Roles.Jeeber, StringComparer.OrdinalIgnoreCase))
            return false;

        try
        {
            var result = await userManagement.RoleSwitchAsync(userId, Roles.Jeeber, ct);
            return string.Equals(result.ActiveRole, Roles.Jeeber, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            log.LogWarning(ex,
                "kyc approve: active-role promotion to '{Role}' failed; the granted role stands "
                + "and the active role is unchanged", Roles.Jeeber);
            return false;
        }
    }
}
