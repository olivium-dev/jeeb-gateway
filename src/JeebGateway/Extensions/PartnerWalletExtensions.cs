using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using JeebGateway.Partner;
using JeebGateway.Partner.Auth;
using JeebGateway.Partner.JeeberSearch;
using JeebGateway.Services.Bff;
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

        // PP-3 free-text jeeber discovery — typed user-management client for
        // GET /v1/partner/jeebers/search. Wired exactly like the sibling UM adapter
        // (HttpUserManagementDualRoleClient, Program.cs): the SAME UserManagementServiceApi:BaseUrl
        // base address (no hardcoded host), the inbound bearer forwarded, and the org-standard Polly
        // v8 resilience pipeline. NO direct DB access — this is the gateway's only seam onto the UM
        // search capability. Lazy/safe base binding: an unset BaseUrl leaves BaseAddress null (dev/CI
        // that do not exercise search) and the client surfaces a clean 502 rather than throwing.
        services
            .AddHttpClient<IPartnerJeeberSearchClient, PartnerJeeberSearchClient>(client =>
            {
                var apiUrl = config["UserManagementServiceApi:BaseUrl"];
                if (!string.IsNullOrWhiteSpace(apiUrl))
                {
                    client.BaseAddress = new Uri(apiUrl);
                }
                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddHttpMessageHandler<BearerForwardingHandler>()
            .AddStandardResilienceHandler();

        // ── PP-1: partner login front door (POST /v1/partner/auth/login) ──────────────────────
        // Admin-provisioned credential roster (no secrets recoverable — SHA-256 hashes only). Validate
        // the top-level section AND each present row at startup (ValidateOnStart) so a malformed roster
        // fails the host loudly, not at first login (dotnet-options-pattern). An EMPTY roster is valid
        // (dev/CI seed the store at runtime through the [DevOnly] hook).
        services
            .AddOptions<PartnerAuthOptions>()
            .Bind(config.GetSection(PartnerAuthOptions.SectionName))
            .Validate(ValidatePartnerAuthRows, "PartnerAuth: one or more provisioned credential rows are invalid.")
            .ValidateOnStart();

        // Singleton so a [DevOnly]-seeded credential persists across scoped requests within a host.
        services.AddSingleton<IPartnerCredentialStore, PartnerCredentialStore>();

        return services;
    }

    /// <summary>
    /// Row-level DataAnnotations validation over the partner credential roster (the top-level
    /// <c>.ValidateDataAnnotations()</c> does not recurse into list items). Empty roster passes.
    /// </summary>
    private static bool ValidatePartnerAuthRows(PartnerAuthOptions options)
        => options.Credentials.All(row =>
        {
            var ctx = new ValidationContext(row);
            return Validator.TryValidateObject(row, ctx, new List<ValidationResult>(), validateAllProperties: true);
        });
}
