using JeebGateway.Services.Bff;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// JEB-67 / T-BE-031 — single entry-point that wires the BFF aggregation
/// concerns: options binding (<see cref="ServiceAuthOptions"/>,
/// <see cref="DownstreamServicesOptions"/>), the startup validator
/// (<see cref="BffStartupValidator"/>), and the <see cref="TimeProvider"/>
/// dependency used by the signing handler.
///
/// Idempotent: TryAdd is used for shared infrastructure (TimeProvider,
/// IHttpContextAccessor) so the existing OTP/JWT registrations in
/// Program.cs continue to win when they ran first.
///
/// Call this BEFORE <see cref="ServiceClientExtensions.AddDownstreamClients"/>
/// so the typed handlers it requires are already registered when the named
/// HttpClient pipelines are built.
/// </summary>
public static class BffServiceCollectionExtensions
{
    public static IServiceCollection AddBffAggregation(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();

        services.AddOptions<ServiceAuthOptions>()
            .Bind(config.GetSection(ServiceAuthOptions.SectionName))
            .ValidateOnStart();

        services.AddOptions<DownstreamServicesOptions>()
            .Bind(config.GetSection(DownstreamServicesOptions.SectionName))
            .ValidateOnStart();

        // BffStartupValidator runs as a hosted service so the host fails
        // BEFORE the first request when required downstream config is
        // missing (AC1). Keeping it as a hosted service (vs ValidateOnStart)
        // lets us surface a single structured error message naming every
        // missing key rather than the first one DataAnnotations bumps into.
        services.AddSingleton<BffStartupValidator>();
        services.AddHostedService(sp => sp.GetRequiredService<BffStartupValidator>());

        return services;
    }
}
