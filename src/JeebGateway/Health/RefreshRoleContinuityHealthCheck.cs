using System.Globalization;
using JeebGateway.Tokens;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace JeebGateway.Health;

/// <summary>Attests the same live owner paths used by refresh; RAM profile counts
/// and remembered successes cannot attest continuity after a restart.</summary>
public sealed class RefreshRoleContinuityHealthCheck(
    IRefreshRoleAuthority authority,
    IRefreshSessionCensus census) : IHealthCheck
{
    internal const string Name = "refresh-role-continuity";

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        bool available;
        try { available = await authority.ProbeAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { available = false; }
        var data = new Dictionary<string, object>
        {
            ["roleSource"] = "user-management-live",
            ["suspensionSource"] = "ban-service-live",
            ["refreshFamiliesObserved"] = census.ActiveFamilies,
            ["rolesEmptyRefreshes"] = census.RolesEmptyRefreshes,
            ["lastRolesEmptyAt"] = census.LastRolesEmptyAt?.ToString("O", CultureInfo.InvariantCulture) ?? "never",
        };
        return available
            ? HealthCheckResult.Healthy("authoritative refresh role and suspension read paths verified", data)
            : HealthCheckResult.Degraded("authoritative refresh role or suspension read path unavailable", data: data);
    }
}
