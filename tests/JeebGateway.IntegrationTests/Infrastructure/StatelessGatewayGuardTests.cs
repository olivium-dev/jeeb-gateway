using System.Reflection;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Infrastructure;

// W5-11 (8cba63b) deleted StoreDurabilityGuard, so the boot-time catalog these cases
// interrogated is gone. What survives is asserted here against its real owner.
public sealed class StatelessGatewayGuardTests
{
    // The gateway-owned background workers, by name. Framework-registered hosted services
    // (OpenTelemetry, DataProtection, health-check publisher, GenericWebHostService) are not
    // the gateway's to ratchet and are excluded by namespace.
    private static readonly string[] GatewayOwnedHostedServices =
    {
        "JeebGateway.Auth.Capabilities.CapabilityCoverageGuard",
        "JeebGateway.Services.Bff.BffStartupValidator",
        "JeebGateway.Conversations.AcceptChatSettleReconciler",
        "JeebGateway.Realtime.CourierPositionPublisher",
        "JeebGateway.Notifications.NotificationDurableWriteStartupAlarm",
        "JeebGateway.Notifications.NewRequestFanoutProcessor",
        // Configuration-only startup guard: validates the mounted service account
        // synchronously and owns no state, timer, queue, or background execution.
        "JeebGateway.Chat.Firebase.FirebaseCustomTokenStartupValidator",
        "JeebGateway.StateService.Work.WorkItemClaimWorker",
        "JeebGateway.Requests.OtpHandover.OtpHandoverSweeper",
        "JeebGateway.Requests.OtpHandover.EscalationMirrorDrainer",
        "JeebGateway.Availability.AvailabilityMirrorDrainer",
        "JeebGateway.Requests.RequestNudgeSweeper",
        "JeebGateway.Requests.RequestExpiryObserver",
        "JeebGateway.Requests.ScheduledDeliveryActivator",
        "JeebGateway.ProhibitedItems.DefaultLexiconSeeder",
        "JeebGateway.Users.DataExport.DataExportProcessor",
        "JeebGateway.Availability.AutoOfflineSweeper",
        // #473 moved the erasure purge clock onto state-service work items,
        // retiring the account-deletion purge worker for the generic sweep worker.
        "JeebGateway.Jobs.DurableWorkSweepWorker",
    };

    private static readonly Assembly Gateway = typeof(Program).Assembly;

    /// <summary>
    /// Successor to Owner_boundary_catalog_has_no_local_or_database_implementation. The catalog
    /// that once vetted durable impls is gone; the claim it protected — the gateway owns no
    /// database — is now a fact about the assembly, which configuration cannot re-arm.
    /// </summary>
    [Fact]
    public void The_Gateway_Assembly_Carries_No_Postgres_Store_And_No_Npgsql_Dependency()
    {
        var typeNames = Gateway.GetTypes().Select(type => type.Name).ToArray();

        // Anti-vacuity: the same scan does find the in-memory stores it is meant to tolerate.
        typeNames.Should().Contain(name => name.StartsWith("InMemory", StringComparison.Ordinal));

        typeNames.Should().NotContain(name => name.Contains("Postgres", StringComparison.Ordinal),
            "W5-11 deleted every gateway-owned Postgres store; an upstream owns the rows now");

        Gateway.GetReferencedAssemblies().Select(reference => reference.Name).Should()
            .NotContain(name => name != null && name.StartsWith("Npgsql", StringComparison.Ordinal),
                "without the driver a re-armed GatewayPostgres DSN cannot open a connection");
    }

    /// <summary>
    /// Successor to Only_startup_and_coverage_validators_are_allowed_as_hosted_services. The
    /// AllowedHostedServices catalog described an aspiration (two validators) the gateway never
    /// reached; the surviving mechanism is the deletion ledger ratchet. Pinning the exact SET,
    /// not a count, is what makes a new background worker fail with its own name.
    /// </summary>
    [Fact]
    public void The_Hosted_Service_Roster_Is_Exactly_The_Ratcheted_Set()
    {
        // The FRAMEWORK factory, not this suite's shadowing one, so the roster is production's.
        using var host = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();

        host.Services.GetServices<IHostedService>()
            .Select(service => service.GetType().FullName!)
            .Where(name => name.StartsWith("JeebGateway.", StringComparison.Ordinal))
            .Should().BeEquivalentTo(GatewayOwnedHostedServices,
                "the deletion ledger ratchets background workers DOWN only; a stateless gateway "
                + "growing a new one must be an explicit decision, not a merge artefact");
    }

    /// <summary>
    /// Successor to Production_configuration_never_accepts_a_direct_delivery_secret. The
    /// surviving owner of that refusal is DeliveryServiceCredentialHandler.
    /// </summary>
    [Fact]
    public async Task Production_Never_Accepts_A_Direct_Delivery_Secret_Only_A_Mounted_File()
    {
        var configuration = Configuration(("DELIVERY_SERVICE_TOKEN", new string('d', 48)));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeliveryServiceCredentialHandler.ReadTokenAsync(
                configuration, new StubEnvironment("Production"), CancellationToken.None));
        refusal.Message.Should().Contain("DELIVERY_SERVICE_TOKEN_FILE");

        // Positive control: the same direct secret IS accepted in development, so the refusal
        // above is the environment gate and not a malformed-token rejection.
        var development = await DeliveryServiceCredentialHandler.ReadTokenAsync(
            configuration, new StubEnvironment("Development"), CancellationToken.None);
        development.Should().HaveLength(48);
    }

    [Fact]
    public async Task A_Relative_Delivery_Token_Path_Is_Refused_Even_In_Development()
    {
        var configuration = Configuration(("DELIVERY_SERVICE_TOKEN_FILE", "relative/secret"));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DeliveryServiceCredentialHandler.ReadTokenAsync(
                configuration, new StubEnvironment("Development"), CancellationToken.None));

        refusal.Message.Should().Contain("absolute mounted-secret path");
    }

    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(
            values.ToDictionary(pair => pair.Key, pair => (string?)pair.Value)).Build();

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "JeebGateway";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
