using JeebGateway.Cms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// WS-01 — single entry-point that wires the gateway-owned CMS authoring plane
/// (W4/W7a). Registers the in-memory surface store as a singleton (authoring
/// state is process-lifetime) and ensures a <see cref="TimeProvider"/> is
/// available for deterministic publish timestamps.
///
/// Idempotent: <c>TryAdd*</c> is used so this composes safely regardless of
/// registration order relative to the BFF/aggregation wiring that also adds
/// <see cref="TimeProvider"/>.
/// </summary>
public static class CmsServiceCollectionExtensions
{
    public static IServiceCollection AddCmsAuthoringPlane(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICmsSurfaceStore>(sp =>
            new InMemoryCmsSurfaceStore(sp.GetService<TimeProvider>()));
        return services;
    }
}
