using FluentAssertions;
using JeebGateway.Health;
using JeebGateway.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.IntegrationTests.Notifications;

/// <summary>
/// The 608debf behaviours, preserved after NotificationCredentialHealthCheck was
/// generalised into ConfiguredCredentialHealthCheck (one row per declared credential).
/// </summary>
public sealed class NotificationCredentialHealthCheckTests
{
    private static readonly GatewayCredentialDeclaration Notification =
        GatewayCredentialDeclarations.All.Single(
            d => d.Name == GatewayCredentialDeclarations.NotificationOwnerName);

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
        result.Description.Should().Contain("ServiceNotificationClient:ApiToken",
            "the resolving rung must be named so a value fallback cannot masquerade as the mounted secret");
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

    [Fact]
    public async Task Native_ApiToken_fallback_behind_an_unmounted_Swarm_path_is_visible()
    {
        // MSI native (PR #522): the ApiToken rung must still resolve, but the
        // unmounted /run/secrets path is now reported instead of silently masked (F6).
        var result = await Check(
            (NotificationRecordWriter.EnabledConfigurationKey, "true"),
            ("ServiceNotificationClient:ServiceTokenFile", "/run/secrets/notification_service_token"),
            ("ServiceNotificationClient:ApiToken", "native-msi-token"));

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should()
            .Contain("ServiceNotificationClient:ApiToken")
            .And.Contain("/run/secrets/notification_service_token");
    }

    private static async Task<HealthCheckResult> Check(
        params (string Key, string Value)[] values)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();
        var check = new ConfiguredCredentialHealthCheck(Notification, configuration);
        return await check.CheckHealthAsync(new HealthCheckContext());
    }
}
