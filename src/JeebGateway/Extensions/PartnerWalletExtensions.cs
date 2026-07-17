using JeebGateway.Partner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Extensions;

/// <summary>
/// DI wiring for the Jeeb Partner Portal wallet BFF (partner-wallet-bff). Keeps Program.cs a
/// one-liner (olivium-gateway-pattern: registration-only Program.cs). Additive — touches no
/// existing registration; reuses the already-registered <c>ServiceWalletClient</c> and
/// <c>IJeebWalletLedgerReader</c>.
/// </summary>
public static class PartnerWalletExtensions
{
    public static IServiceCollection AddPartnerWallet(this IServiceCollection services, IConfiguration config)
    {
        // Validated options — fail the host loudly at startup on a mis-configured PartnerWallet
        // section rather than at first money move (dotnet-options-pattern). No secrets here.
        services
            .AddOptions<PartnerWalletOptions>()
            .Bind(config.GetSection(PartnerWalletOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Scoped: depends on the scoped ServiceWalletClient.
        services.AddScoped<IPartnerWalletService, PartnerWalletService>();

        return services;
    }
}
