using JeebGateway.Users.SavedLocations;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace JeebGateway.Extensions;

/// <summary>
/// WS-02 — DI wiring for the Saved Locations BFF (ACCT-04 / REQ-02). One call
/// added to Program.cs keeps the hot file to a single line and isolates this
/// feature's registration here.
///
/// <para>DURABLE by default in production: when a gateway-postgres connection
/// string is configured (<c>GatewayPostgres:ConnectionString</c>, the same key
/// that makes the COD settlement ledger durable), the store is
/// <see cref="PostgresSavedLocationStore"/> so saved delivery addresses survive
/// a gateway restart/redeploy. When the connection string is absent (local dev /
/// CI without Postgres), it falls back to <see cref="InMemorySavedLocationStore"/>
/// so the vertical stays exercisable — exactly mirroring how
/// <c>ISettlementStore</c> degrades.</para>
///
/// Idempotent via <c>TryAddSingleton</c> so a future remote registration that
/// runs first continues to win.
/// </summary>
public static class SavedLocationsServiceCollectionExtensions
{
    public static IServiceCollection AddSavedLocations(this IServiceCollection services, string? gatewayPostgresConnectionString = null)
    {
        if (!string.IsNullOrWhiteSpace(gatewayPostgresConnectionString))
        {
            // Durable: INpgsqlConnectionFactory is registered in Program.cs on the
            // same connection string, so the Postgres store can resolve it.
            services.TryAddSingleton<ISavedLocationStore, PostgresSavedLocationStore>();
        }
        else
        {
            services.TryAddSingleton<ISavedLocationStore, InMemorySavedLocationStore>();
        }
        return services;
    }
}
