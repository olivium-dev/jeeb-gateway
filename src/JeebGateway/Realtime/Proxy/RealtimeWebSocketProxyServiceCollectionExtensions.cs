using Microsoft.Extensions.Configuration;

namespace JeebGateway.Realtime.Proxy;

internal static class RealtimeWebSocketProxyServiceCollectionExtensions
{
    public static IServiceCollection AddRealtimeWebSocketProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RealtimeWebSocketProxyOptions>()
            .Bind(configuration.GetSection(RealtimeWebSocketProxyOptions.SectionName));

        services.AddHttpForwarder();
        services.AddLogging(logging =>
            logging.AddFilter("Yarp.ReverseProxy", LogLevel.None));
        services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(
            options => options.AddServerHeader = false);
        services.AddSingleton<IRealtimeProxyDestinationResolver,
            RealtimeProxyDestinationResolver>();
        services.AddSingleton<RealtimeWebSocketProxyTransport>();
        services.AddSingleton<RealtimeWebSocketProxyTransformer>();
        services.AddSingleton<RealtimeWebSocketProxyConcurrencyLimiter>();
        services.AddSingleton<RealtimeWebSocketProxyMetrics>();
        return services;
    }
}
