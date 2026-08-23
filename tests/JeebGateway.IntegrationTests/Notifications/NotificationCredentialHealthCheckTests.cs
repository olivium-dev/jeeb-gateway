using FluentAssertions;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Notifications;

public sealed class NotificationCredentialHealthCheckTests
{
    [Fact]
    public async Task Durable_writes_disarmed_reports_healthy()
    {
        var result = await Check();

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Armed_with_resolvable_credential_reports_healthy()
    {
        var result = await Check(
            (NotificationRecordWriter.EnabledConfigurationKey, "true"),
            ("ServiceNotificationClient:ApiToken", "health-check-token"));

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task Armed_without_any_credential_reports_unhealthy_naming_keys()
    {
        var result = await Check(
            (NotificationRecordWriter.EnabledConfigurationKey, "true"),
            ("ServiceNotificationClient:ServiceTokenFile", "/run/secrets/notification_service_token"));

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should()
            .Contain("/run/secrets/notification_service_token")
            .And.Contain("ServiceNotificationClient:ApiToken");
    }

    private static async Task<HealthCheckResult> Check(
        params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();
        var check = new NotificationCredentialHealthCheck(
            configuration,
            NullLogger<NotificationCredentialHealthCheck>.Instance);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }
}
