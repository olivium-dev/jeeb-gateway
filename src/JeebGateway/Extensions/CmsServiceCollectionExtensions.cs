using JeebGateway.Cms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// Local-harness wiring for the retired gateway-owned CMS authoring plane.
/// Production does not register this extension: the essential back-office has no
/// content-management module and the gateway must not own authoring state.
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
