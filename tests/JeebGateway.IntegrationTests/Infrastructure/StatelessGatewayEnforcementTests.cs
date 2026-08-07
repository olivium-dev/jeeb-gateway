using System.Reflection;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Infrastructure;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

public sealed class StatelessGatewayEnforcementTests
{
    public static TheoryData<string> ForbiddenDatabaseKeys => new()
    {
        "GatewayPostgres:ConnectionString",
        "WalletPostgres:ConnectionString",
        "ConnectionStrings:Default",
        "DATABASE_URL",
        "JEEB_DATABASE_URL",
    };

    [Theory]
    [MemberData(nameof(ForbiddenDatabaseKeys))]
    public void Production_rejects_every_historic_database_credential(string key)
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            [StatelessGatewayGuard.CodOwnerVerifiedKey] = "true",
            [key] = "must-never-reach-the-gateway",
        });

        StatelessGatewayGuard.Evaluate(services, new TestEnvironment("Production"))
            .Should().Contain(message => message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_an_unverified_cod_owner()
    {
        using var services = BuildServices(new Dictionary<string, string?>());

        StatelessGatewayGuard.Evaluate(services, new TestEnvironment("Production"))
            .Should().Contain(message => message.Contains(
                StatelessGatewayGuard.CodOwnerVerifiedKey,
                StringComparison.Ordinal));
    }

    [Fact]
    public void Production_rejects_process_local_state_contracts()
    {
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IConfiguration>(Configuration(new Dictionary<string, string?>
        {
            [StatelessGatewayGuard.CodOwnerVerifiedKey] = "true",
        }));
        registrations.AddSingleton<IRefreshTokenStore, InMemoryRefreshTokenStore>();
        using var services = registrations.BuildServiceProvider();

        StatelessGatewayGuard.Evaluate(services, new TestEnvironment("Production"))
            .Should().Contain(message => message.Contains(
                nameof(InMemoryRefreshTokenStore),
                StringComparison.Ordinal));
    }

    [Fact]
    public void Local_test_harness_is_exempt_from_the_production_owner_gate()
    {
        using var services = BuildServices(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "test-fixture-only",
        });

        StatelessGatewayGuard.Evaluate(services, new TestEnvironment("Testing"))
            .Should().BeEmpty();
    }

    [Fact]
    public void Shipped_gateway_assembly_has_no_database_provider_reference()
    {
        typeof(Program).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .Should().NotContain(name =>
                string.Equals(name, "Npgsql", StringComparison.OrdinalIgnoreCase)
                || name != null && name.Contains(
                    "EntityFrameworkCore", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Production_controller_filter_keeps_essential_admin_and_auth_only()
    {
        var feature = new ControllerFeature();
        feature.Controllers.Add(typeof(AdminCasesController).GetTypeInfo());
        feature.Controllers.Add(typeof(AuthController).GetTypeInfo());
        feature.Controllers.Add(typeof(CodSettlementComposeController).GetTypeInfo());
        feature.Controllers.Add(typeof(UserController).GetTypeInfo());

        new EssentialGatewayControllerFeatureProvider()
            .PopulateFeature(Array.Empty<ApplicationPart>(), feature);

        feature.Controllers.Select(type => type.AsType()).Should().BeEquivalentTo(new[]
        {
            typeof(AdminCasesController),
            typeof(AuthController),
        });
    }

    private static ServiceProvider BuildServices(IReadOnlyDictionary<string, string?> values)
    {
        var registrations = new ServiceCollection();
        registrations.AddSingleton<IConfiguration>(Configuration(values));
        return registrations.BuildServiceProvider();
    }

    private static IConfiguration Configuration(IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed class TestEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "JeebGateway.IntegrationTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
