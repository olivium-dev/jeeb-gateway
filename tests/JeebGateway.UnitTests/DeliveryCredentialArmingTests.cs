using FluentAssertions;
using JeebGateway.Health;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class DeliveryCredentialArmingTests
{
    private static GatewayCredentialDeclaration Declaration =>
        GatewayCredentialDeclarations.All.Single(row => row.Name == "credential-delivery-service-token");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task NoOwnerUrlAndDisabledDeliveryRemainsHonestlyUnarmed(string? url)
    {
        var config = Configuration(url, false);
        Declaration.IsArmed(config).Should().BeFalse();
        var result = await CheckAsync(config);
        result.Status.Should().Be(HealthStatus.Healthy);
        result.Description.Should().StartWith("not armed");
    }

    [Fact]
    public async Task EnabledDeliveryStillRequiresCredentialEvenWithoutOwnerUrl()
    {
        var config = Configuration(null, true);
        Declaration.IsArmed(config).Should().BeTrue();
        (await CheckAsync(config)).Status.Should().Be(HealthStatus.Degraded);
    }

    [Theory]
    [InlineData("FeatureFlags:OtpEscalationsMode", "local")]
    [InlineData("FeatureFlags:OtpEscalationsMode", "upstream-authority")]
    [InlineData("FeatureFlags:AvailabilityMode", "local")]
    [InlineData("FeatureFlags:AvailabilityMode", "upstream-authority")]
    [InlineData("FeatureFlags:TiersMode", "local")]
    [InlineData("FeatureFlags:TiersMode", "upstream-authority")]
    public async Task IndependentOwnerClientsRequireCredentialRegardlessOfDeliveryFlag(
        string modeKey, string mode)
    {
        var config = Configuration("http://delivery.invalid:8080", false, modeKey, mode);
        Declaration.IsArmed(config).Should().BeTrue();
        var result = await CheckAsync(config);
        result.Status.Should().Be(HealthStatus.Degraded,
            "configured cases, requests-owner and admin clients do not depend on the delivery flag");
        result.Description.Should().Contain("no source configured while armed");
    }

    [Fact]
    public async Task ConfiguredOwnerWithMountedCredentialResolvesWhileDeliveryFlagIsFalse()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, new string('x', 32));
            var config = Configuration("http://delivery.invalid:8080", false);
            config["DELIVERY_SERVICE_TOKEN_FILE"] = file;
            var result = await CheckAsync(config);
            result.Status.Should().Be(HealthStatus.Healthy);
            result.Description.Should().Be("resolved from DELIVERY_SERVICE_TOKEN_FILE");
        }
        finally { File.Delete(file); }
    }

    private static IConfigurationRoot Configuration(string? url, bool enabled,
        string modeKey = "FeatureFlags:TiersMode", string mode = "local") =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
            ["Services:Delivery:BaseUrl"] = url,
            ["FeatureFlags:UseUpstream:Delivery"] = enabled.ToString(),
            [modeKey] = mode
        }).Build();

    private static Task<HealthCheckResult> CheckAsync(IConfiguration configuration) =>
        new ConfiguredCredentialHealthCheck(Declaration, configuration)
            .CheckHealthAsync(new HealthCheckContext());
}
