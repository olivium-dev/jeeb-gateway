using FluentAssertions;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

public sealed class StatelessGatewayGuardTests
{
    [Fact]
    public void Owner_boundary_catalog_has_no_local_or_database_implementation()
    {
        StoreDurabilityGuard.Critical.Should().NotBeEmpty();
        StoreDurabilityGuard.KnownInMemoryBacklog.Should().BeEmpty();
        StoreDurabilityGuard.UpstreamContractIncomplete.Should().BeEmpty();

        StoreDurabilityGuard.Critical
            .SelectMany(entry => entry.DurableImpls)
            .Select(type => type.Name)
            .Should().OnlyContain(name =>
                !name.Contains("InMemory", StringComparison.Ordinal)
                && !name.Contains("Postgres", StringComparison.Ordinal)
                && !name.Contains("Npgsql", StringComparison.Ordinal));
    }

    [Fact]
    public void Owner_boundary_catalog_accepts_only_its_explicit_owner_adapters()
    {
        var approved = StoreDurabilityGuard.Critical.ToDictionary(
            entry => entry.Iface,
            entry => entry.DurableImpls[0]);

        StoreDurabilityGuard.Evaluate(type => approved[type]).Should().BeEmpty();
        StoreDurabilityGuard.Evaluate(_ => typeof(object)).Should().HaveCount(approved.Count);
    }

    [Fact]
    public async Task Production_configuration_requires_mounted_owner_credentials_and_rejects_db_or_upg()
    {
        var stateToken = Path.GetTempFileName();
        var deliveryToken = Path.GetTempFileName();
        var notificationToken = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(deliveryToken, new string('d', 48) + "\n");
            var clean = Configuration(
                ("JeebStateService:ServiceTokenFile", stateToken),
                ("Services:Delivery:ServiceTokenFile", deliveryToken),
                ("ServiceNotificationClient:ServiceTokenFile", notificationToken));
            StoreDurabilityGuard.EvaluateConfiguration(clean).Should().BeEmpty();

            var forbidden = Configuration(
                ("JeebStateService:ServiceTokenFile", stateToken),
                ("Services:Delivery:ServiceTokenFile", deliveryToken),
                ("ServiceNotificationClient:ServiceTokenFile", notificationToken),
                ("ConnectionStrings:GatewayPostgres", "Host=forbidden"),
                ("UPG_BASE_URL", "https://retired.example"));

            StoreDurabilityGuard.EvaluateConfiguration(forbidden).Should().Contain(violation =>
                violation.Contains("GatewayPostgres", StringComparison.Ordinal));
            StoreDurabilityGuard.EvaluateConfiguration(forbidden).Should().Contain(violation =>
                violation.Contains("UPG_BASE_URL", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(stateToken);
            File.Delete(deliveryToken);
            File.Delete(notificationToken);
        }
    }

    [Theory]
    [InlineData("GatewayPostgres:ConnectionString")]
    [InlineData("WalletPostgres:ConnectionString")]
    [InlineData("ConnectionStrings:GatewayPostgres")]
    [InlineData("ConnectionStrings:WalletPostgres")]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("DATABASE_URL")]
    [InlineData("JEEB_DATABASE_URL")]
    [InlineData("UnifiedPaymentGateway:BaseUrl")]
    [InlineData("UPG:BaseUrl")]
    [InlineData("UPG_BASE_URL")]
    public async Task Every_database_or_upg_configuration_alias_is_rejected(string key)
    {
        var stateToken = Path.GetTempFileName();
        var deliveryToken = Path.GetTempFileName();
        var notificationToken = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(deliveryToken, new string('d', 48) + "\n");
            var configuration = Configuration(
                ("JeebStateService:ServiceTokenFile", stateToken),
                ("DELIVERY_SERVICE_TOKEN_FILE", deliveryToken),
                ("ServiceNotificationClient:ServiceTokenFile", notificationToken),
                (key, "forbidden"));

            StoreDurabilityGuard.EvaluateConfiguration(configuration)
                .Should().Contain(violation => violation.Contains(key, StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(stateToken);
            File.Delete(deliveryToken);
            File.Delete(notificationToken);
        }
    }

    [Fact]
    public void Only_startup_and_coverage_validators_are_allowed_as_hosted_services()
    {
        StoreDurabilityGuard.AllowedHostedServices.Should().BeEquivalentTo(new[]
        {
            typeof(JeebGateway.Auth.Capabilities.CapabilityCoverageGuard),
            typeof(JeebGateway.Services.Bff.BffStartupValidator),
        });
        StoreDurabilityGuard.EvaluateHostedServices(
                new IHostedService[] { new UnexpectedWorker() })
            .Should().ContainSingle()
            .Which.Should().Contain(nameof(UnexpectedWorker));
    }

    [Fact]
    public void Production_configuration_never_accepts_a_direct_delivery_secret()
    {
        var stateToken = Path.GetTempFileName();
        try
        {
            var configuration = Configuration(
                ("JeebStateService:ServiceTokenFile", stateToken),
                ("DELIVERY_SERVICE_TOKEN", new string('d', 48)),
                ("NOTIFICATION_SERVICE_TOKEN", new string('n', 48)));

            StoreDurabilityGuard.EvaluateConfiguration(configuration).Should().Contain(violation =>
                violation.Contains("DELIVERY_SERVICE_TOKEN_FILE", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(stateToken);
        }
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();

    private sealed class UnexpectedWorker : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
