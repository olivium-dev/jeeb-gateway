using System.Globalization;
using JeebGateway.Tokens;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>
/// G5 / D2 §4a — the pre-incident alarm for the roles-loss class. <c>IUsersStore</c> is process RAM
/// (durability register #8 is not armed) while the refresh store is durable, so after any restart
/// the store is empty for every user while live sessions keep rotating against it. Before G5 that
/// silently minted roles-less tokens and 403'd every capability route with <c>/health/ready</c>
/// still green; G5 made it a fail-closed 401. Either way the operator needs to SEE it.
/// </summary>
public sealed class RefreshRoleContinuityHealthCheck(
    IUsersStoreCensus users,
    IRefreshSessionCensus census) : IHealthCheck
{
    internal const string Name = "refresh-role-continuity";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
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
