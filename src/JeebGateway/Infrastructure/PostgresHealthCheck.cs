using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace JeebGateway.Infrastructure;

/// <summary>
/// GW12-OBS-2 (Leg-12) — readiness health check for a gateway-owned Postgres
/// database. The durability Leg made 9+ stores depend on <c>GatewayPostgres</c>
/// (and the wallet path on <c>WalletPostgres</c>), yet <c>/health/ready</c> and
/// <c>/health/aggregate</c> had NO probe against these databases — a DB outage,
/// pool exhaustion, or rotated credentials left every durable read/write throwing
/// while the readiness surface stayed green. This check closes that blind spot.
///
/// <para>Deliberately a small custom <see cref="IHealthCheck"/> (mirroring the
/// existing <see cref="JeebGateway.Whisper.WhisperHealthCheck"/> pattern) rather
/// than pulling in the <c>AspNetCore.HealthChecks.Npgsql</c> package: Npgsql is
/// already referenced, and a hand-rolled <c>SELECT 1</c> keeps the dependency
/// graph and connection idiom identical to the durable stores (raw
/// <see cref="NpgsqlConnection"/>, one connection per probe, driver-pooled).</para>
///
/// <para>Registered with the <c>"ready"</c> tag so it gates <c>/health/ready</c>
/// (readiness), never <c>/health/live</c> (liveness) — a DB blip must not pull the
/// stateless gateway process out of rotation, matching the documented liveness /
/// readiness split. A connection or query failure maps to
/// <see cref="HealthStatus.Unhealthy"/>; the exception message is captured in the
/// check's <c>description</c> for the ops dashboard but never surfaced to clients.</para>
/// </summary>
public sealed class PostgresHealthCheck : IHealthCheck
{
    private readonly string _connectionString;
    private readonly string _databaseLabel;

    /// <param name="connectionString">The Npgsql connection string to probe.</param>
    /// <param name="databaseLabel">
    /// A human label for the database (e.g. <c>GatewayPostgres</c>) used in the
    /// health description. NEVER include the connection string here — it may carry
    /// credentials.
    /// </param>
    public PostgresHealthCheck(string connectionString, string databaseLabel)
    {
        _connectionString = connectionString;
        _databaseLabel = databaseLabel;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(cancellationToken);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy($"{_databaseLabel} reachable");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Probe was cancelled (shutdown / health-check timeout) — surface as
            // Unhealthy rather than letting the cancellation bubble as an unhandled
            // fault in the readiness pipeline.
            return HealthCheckResult.Unhealthy($"{_databaseLabel} probe cancelled");
        }
        catch (Exception ex)
        {
            // Message only (no stack, no connection string) — enough for an on-call
            // engineer to distinguish "pool exhausted" / "auth failed" / "host down"
            // on the aggregate dashboard.
            return HealthCheckResult.Unhealthy(
                $"{_databaseLabel} unreachable: {ex.Message}");
        }
    }
}
