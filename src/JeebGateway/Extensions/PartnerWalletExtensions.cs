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

        // Money-safety idempotency / dedup + immutable cash-in audit store. MONEY: its whole contract
        // is "a retried confirm never double-moves money", so an in-memory fallback is a data-loss
        // hole on a money path and is refused fail-closed in prod-like envs
        // (StoreDurabilityGuard.Critical). Postgres-backed (partner_wallet_operations, migration 0040)
        // whenever GatewayPostgres is configured — reusing the already-registered
        // INpgsqlConnectionFactory (see Program.cs) — exactly like ISettlementEnqueueStore; in-memory
        // fallback for dev/CI/test only. Singleton so the claim state persists across scoped requests.
        var gatewayPostgresCs = config["GatewayPostgres:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(gatewayPostgresCs))
        {
            services.AddSingleton<IPartnerWalletOperationStore, PostgresPartnerWalletOperationStore>();
        }
        else
        {
            services.AddSingleton<IPartnerWalletOperationStore, InMemoryPartnerWalletOperationStore>();
        }

        // Scoped: depends on the scoped ServiceWalletClient.
        services.AddScoped<IPartnerWalletService, PartnerWalletService>();

        return services;
    }
}
