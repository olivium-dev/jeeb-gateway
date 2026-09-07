using System.Globalization;
using JeebGateway.Tokens;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>
/// Runtime readiness attests the same owner role and suspension paths used by
/// refresh. The legacy constructor seam retains the original local-store alarm
/// for isolated tests. G5 / D2 §4a: <c>IUsersStore</c> is process RAM
/// (durability register #8 is not armed) while the refresh store is durable, so after any restart
/// the store is empty for every user while live sessions keep rotating against it. Before G5 that
/// silently minted roles-less tokens and 403'd every capability route with <c>/health/ready</c>
/// still green; G5 made it a fail-closed 401. Either way the operator needs to SEE it.
/// </summary>
public sealed class RefreshRoleContinuityHealthCheck(
    IUsersStoreCensus users,
    IRefreshSessionCensus census,
    IRefreshRoleAuthority? authority = null) : IHealthCheck
{
    internal const string Name = "refresh-role-continuity";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (authority is not null)
        {
            bool available;
            try { available = await authority.ProbeAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { available = false; }
            var ownerData = new Dictionary<string, object>
            {
                ["roleSource"] = "user-management-live",
                ["suspensionSource"] = "ban-service-live",
                ["refreshFamiliesObserved"] = census.ActiveFamilies,
                ["rolesEmptyRefreshes"] = census.RolesEmptyRefreshes,
                ["lastRolesEmptyAt"] = census.LastRolesEmptyAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never",
            };
            return available
                ? HealthCheckResult.Healthy("authoritative refresh role and suspension read paths verified", ownerData)
                : HealthCheckResult.Degraded("authoritative refresh role or suspension read path unavailable", data: ownerData);
        }

        int profiles;
        try
        {
            profiles = await users.CountProfilesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Degraded(
                "users-store profile count unavailable, so role continuity cannot be asserted", ex);
        }

        var families = census.ActiveFamilies;
        var rolesEmpty = census.RolesEmptyRefreshes;
        var lastRolesEmpty = census.LastRolesEmptyAt;

        var data = new Dictionary<string, object>
        {
            ["usersStoreProfiles"] = profiles,
            ["refreshFamiliesActive"] = families,
            ["rolesEmptyRefreshes"] = rolesEmpty,
            ["lastRolesEmptyAt"] = lastRolesEmpty?.ToString("O", CultureInfo.InvariantCulture) ?? "never",
        };

        var summary =
            $"usersStoreProfiles={profiles} refreshFamiliesActive={families} "
            + $"rolesEmptyRefreshes={rolesEmpty} "
            + $"lastRolesEmptyAt={data["lastRolesEmptyAt"]}";

        if (profiles == 0 && families > 0)
        {
            return HealthCheckResult.Degraded(
                "refresh role continuity at risk: the users store holds no profiles while sessions "
                + $"are rotating against it, so every rotation resolves no roles. {summary}",
                data: data);
        }

        return HealthCheckResult.Healthy(summary, data);
    }
}
