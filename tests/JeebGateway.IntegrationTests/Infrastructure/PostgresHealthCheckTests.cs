using FluentAssertions;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// GW12-OBS-2 — <see cref="PostgresHealthCheck"/> unit tests. The check must map an
/// unreachable database to <see cref="HealthStatus.Unhealthy"/> WITHOUT throwing (so a
/// DB outage degrades the readiness surface rather than faulting the health pipeline),
/// and must never surface the connection string. A live-Postgres happy-path assertion is
/// deferred to Testcontainers-QV, matching this project's established no-Testcontainers
/// convention for DB-touching store tests.
/// </summary>
public class PostgresHealthCheckTests
{
    // Port 1 refuses immediately; short timeouts keep the test fast and deterministic.
    private const string UnreachableCs =
        "Host=127.0.0.1;Port=1;Database=nope;Username=x;Password=y;Timeout=1;Command Timeout=1";

    [Fact]
    public async Task Returns_Unhealthy_When_Database_Unreachable()
    {
        var check = new PostgresHealthCheck(UnreachableCs, "GatewayPostgres");

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("GatewayPostgres");
    }

    [Fact]
    public async Task Unhealthy_Description_Never_Leaks_Connection_String()
    {
        var check = new PostgresHealthCheck(UnreachableCs, "WalletPostgres");

        var result = await check.CheckHealthAsync(new HealthCheckContext(), CancellationToken.None);

        result.Status.Should().Be(HealthStatus.Unhealthy);
        // The label is included; the raw connection string (credentials) must not be.
        result.Description.Should().Contain("WalletPostgres");
        result.Description!.Should().NotContain("Password=y");
        result.Description.Should().NotContain("Username=x");
    }

    [Fact]
    public async Task Does_Not_Throw_On_Cancellation()
    {
        var check = new PostgresHealthCheck(UnreachableCs, "GatewayPostgres");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Pre-cancelled token: the check must return a result, not surface the OCE.
        var result = await check.CheckHealthAsync(new HealthCheckContext(), cts.Token);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }
}
