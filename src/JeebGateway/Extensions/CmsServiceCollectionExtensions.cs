using JeebGateway.Cms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// WS-01 — single entry-point that wires the gateway-owned CMS authoring plane
/// (W4/W7a). Ensures a <see cref="TimeProvider"/> is available for deterministic
/// publish timestamps and selects the surface store:
/// <list type="bullet">
///   <item><see cref="PostgresCmsSurfaceStore"/> (cms_surfaces +
///     cms_surface_versions, migration 0032) whenever
///     <c>GatewayPostgres:ConnectionString</c> is configured — the durable
///     system of record for CMS surfaces / drafts / published versions
///     (JEBV4-132, AUDIT-A IN-MEM-LIVE). Requires the
///     <see cref="JeebGateway.Infrastructure.INpgsqlConnectionFactory"/> that
///     Program.cs registers inside the same GatewayPostgres block (resolution
///     is at container-build time, so registration order does not matter).</item>
///   <item><see cref="InMemoryCmsSurfaceStore"/> otherwise — the dev / CI / test
///     fallback (authoring state is process-lifetime), keeping local runs and the
///     integration-test harness unchanged. This is the established
///     FAIL-OPEN-then-gate pattern; StoreDurabilityGuard enforces the Postgres
///     store in prod-like environments.</item>
/// </list>
///
/// Idempotent: <c>TryAdd*</c> is used so this composes safely regardless of
/// registration order relative to the BFF/aggregation wiring that also adds
/// <see cref="TimeProvider"/>.
/// </summary>
public static class CmsServiceCollectionExtensions
{
    public static IServiceCollection AddCmsAuthoringPlane(
        this IServiceCollection services,
        string? gatewayPostgresConnectionString = null)
    {
        services.TryAddSingleton(TimeProvider.System);

        if (!string.IsNullOrWhiteSpace(gatewayPostgresConnectionString))
        {
            services.TryAddSingleton<ICmsSurfaceStore, PostgresCmsSurfaceStore>();
        }
        else
        {
            services.TryAddSingleton<ICmsSurfaceStore>(sp =>
                new InMemoryCmsSurfaceStore(sp.GetService<TimeProvider>()));
        }

        return services;
    }
}
