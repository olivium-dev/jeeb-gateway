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
/// <see cref="HealthStatus.Unhealthy"/> with a GENERIC description
/// (<c>"{label} unreachable"</c>); the underlying exception (whose Npgsql message can
/// carry the DB host:port) is attached to the result for SERVER-SIDE capture only and is
/// never serialized to the AllowAnonymous <c>/health/ready</c> / <c>/health/aggregate</c>
/// response.</para>
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
            // F3: GENERIC client-facing description — no ex.Message. Npgsql connect failures
            // embed the DB host:port ("Failed to connect to 10.x.x.x:5432") in ex.Message, and
            // /health/ready + /health/aggregate are AllowAnonymous, so interpolating ex.Message
            // into the description leaked the database host to any unauthenticated caller. The
            // real exception is attached to the HealthCheckResult so it is captured SERVER-SIDE
            // only (OTel span via HealthCheckPublisher / logs) — never serialized to the anon
            // response (AggregateHealthResponseWriter no longer emits raw exception messages).
            return HealthCheckResult.Unhealthy(
                description: $"{_databaseLabel} unreachable",
                exception: ex);
        }
    }
}
