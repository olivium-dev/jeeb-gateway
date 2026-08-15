using JeebGateway.Cms;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// W4/W7a ownership boundary. CMS routes stay in the gateway for compatibility,
/// while bundler-service owns every document, draft, version, and publication.
/// The registered adapter is stateless; there is deliberately no in-process or
/// gateway-Postgres fallback.
/// </summary>
public static class CmsServiceCollectionExtensions
{
    public static IServiceCollection AddCmsAuthoringPlane(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ServiceClientExtensions.AttachResilienceOnly(
            services.AddHttpClient(BundlerCmsSurfaceStore.HttpClientName, client =>
            {
                var baseUrl = configuration[BundlerCmsSurfaceStore.BaseUrlConfigurationKey];
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
                    || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"{BundlerCmsSurfaceStore.BaseUrlConfigurationKey} must be an absolute HTTP(S) URL.");
                client.BaseAddress = new Uri(uri.ToString().TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(30);
            }));
        services.TryAddSingleton<ICmsSurfaceStore, BundlerCmsSurfaceStore>();

        return services;
    }
}
