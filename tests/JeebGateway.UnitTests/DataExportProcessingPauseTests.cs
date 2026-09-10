using System.Text.Json;
using FluentAssertions;
using JeebGateway.Artifacts;
using JeebGateway.Health;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class DataExportProcessingPauseTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
    private static IOptions<DataExportOptions> Paused() =>
        Options.Create(new DataExportOptions { Enabled = true, ProcessingEnabled = false });

    [Fact]
    public void Policy_is_an_immutable_snapshot_and_defaults_to_processing_enabled()
    {
        new DataExportProcessingPolicy(Options.Create(new DataExportOptions())).IsPaused.Should().BeFalse();
        var options = Paused();
        var policy = new DataExportProcessingPolicy(options);
        options.Value.ProcessingEnabled = true;
        policy.IsPaused.Should().BeTrue("changing configuration must not release work mid-activation");
    }

    [Fact]
    public async Task Paused_executor_never_calls_state_or_handler_even_for_invalid_limits()
    {
        var work = Substitute.For<IStateWorkItemClient>();
        var handler = Substitute.For<IDurableWorkItemHandler>();
        handler.Kind.Returns(DurableWorkContract.DataExportKind);
        var executor = Executor(work, handler, Paused());
        handler.ClearReceivedCalls();

        Func<Task> sweep = () => executor.SweepAsync(DurableWorkContract.DataExportKind, 0, default);
        await sweep.Should().ThrowAsync<DataExportProcessingPausedException>();
        work.ReceivedCalls().Should().BeEmpty("not even claims, lease renewals, retry/terminal CAS or reads are allowed");
        handler.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData(true, "data-export")]
    [InlineData(false, "account-deletion")]
    public async Task Enabled_export_and_unrelated_deletion_keep_their_claim_path(bool enabled, string kind)
    {
        kind.Should().BeOneOf(DurableWorkContract.DataExportKind, DurableWorkContract.AccountDeletionKind);
        var work = Substitute.For<IStateWorkItemClient>();
        work.ClaimAsync(Arg.Any<StateWorkClaim>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<StateWorkItem>());
        var handler = Substitute.For<IDurableWorkItemHandler>();
        handler.Kind.Returns(kind);
        var executor = Executor(work, handler, Options.Create(new DataExportOptions { ProcessingEnabled = enabled }));
        var result = await executor.SweepAsync(kind, null, default);
        result.Claimed.Should().Be(0);
        await work.Received(1).ClaimAsync(Arg.Is<StateWorkClaim>(claim => claim.Kinds != null && claim.Kinds.Single() == kind), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Direct_handler_pause_precedes_every_owner_signing_and_notification_effect()
    {
        var packager = Substitute.For<IDataExportPackager>();
        var notifier = Substitute.For<IDataExportNotifier>();
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        var tokens = Substitute.For<IDataExportTokenProtector>();
        var options = Paused();
        var handler = new DataExportWorkHandler(packager, notifier, artifacts, tokens, options,
            TimeProvider.System, new DataExportProcessingPolicy(options));
        Func<Task> run = () => handler.ExecuteAsync(Item("leased"), default);
        await run.Should().ThrowAsync<DataExportProcessingPausedException>();
        packager.ReceivedCalls().Should().BeEmpty();
        notifier.ReceivedCalls().Should().BeEmpty();
        artifacts.ReceivedCalls().Should().BeEmpty();
        tokens.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Legacy_hosted_and_direct_processor_pause_before_scope_resolution()
    {
        var services = Substitute.For<IServiceProvider>();
        var options = Paused();
        using var processor = new DataExportProcessor(services, TimeProvider.System, options,
            NullLogger<DataExportProcessor>.Instance, new DataExportProcessingPolicy(options));
        await processor.StartAsync(default);
        (await processor.ProcessOnceAsync(default)).Should().Be(0);
        await processor.StopAsync(default);
        services.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Hosted_durable_tick_skips_paused_exports_but_keeps_deletion_driver_running()
    {
        var work = Substitute.For<IStateWorkItemClient>();
        work.ClaimAsync(Arg.Any<StateWorkClaim>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<StateWorkItem>());
        var handler = Substitute.For<IDurableWorkItemHandler>();
        handler.Kind.Returns(DurableWorkContract.AccountDeletionKind);
        using var services = new ServiceCollection()
            .AddScoped(_ => Executor(work, handler, Paused())).BuildServiceProvider();
        using var worker = new DurableWorkSweepWorker(services, Options.Create(new DurableWorkSweepOptions()),
            TimeProvider.System, NullLogger<DurableWorkSweepWorker>.Instance);
        await worker.StartAsync(default);
        await worker.StopAsync(default);
        await work.Received(1).ClaimAsync(Arg.Is<StateWorkClaim>(claim =>
            claim.Kinds != null && claim.Kinds.Single() == DurableWorkContract.AccountDeletionKind), Arg.Any<CancellationToken>());
        await work.DidNotReceive().ClaimAsync(Arg.Is<StateWorkClaim>(claim =>
            claim.Kinds != null && claim.Kinds.Contains(DurableWorkContract.DataExportKind)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("credential-internal-job-token", "InternalJobAuth:TokenFile")]
    [InlineData("credential-private-artifact-store-bearer", "PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE")]
    [InlineData("credential-data-export-signing-key", "DATA_EXPORT_TOKEN_SIGNING_KEY_FILE")]
    public async Task Pause_never_disarms_missing_or_unresolvable_credentials(string name, string key)
    {
        var data = new Dictionary<string, string?>
        {
            ["Users:DataExport:Enabled"] = "true",
            ["Users:DataExport:ProcessingEnabled"] = "false",
            ["PRIVATE_ARTIFACT_STORE_BASE_URL"] = "http://owner.invalid/"
        };
        var declaration = GatewayCredentialDeclarations.All.Single(row => row.Name == name);
        var missing = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        declaration.IsArmed(missing).Should().BeTrue();
        var result = await new ConfiguredCredentialHealthCheck(declaration, missing).CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Degraded);
        data[key] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent-purpose-key");
        var unreadable = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        result = await new ConfiguredCredentialHealthCheck(declaration, unreadable).CheckHealthAsync(new HealthCheckContext());
        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Public_request_and_status_remain_available_and_do_not_claim_when_processing_paused()
    {
        var work = Substitute.For<IStateWorkItemClient>();
        var item = Item("queued");
        work.CreateAsync(Arg.Any<string>(), Arg.Any<StateWorkItemCreate>(), Arg.Any<CancellationToken>()).Returns(item);
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        var tokens = Substitute.For<IDataExportTokenProtector>();
        var workflow = new StateDataExportWorkflow(work, artifacts, tokens, Paused(), TimeProvider.System);
        (await workflow.RequestAsync("synthetic-user", DataExportFormat.Json, default)).Status.Should().Be(DataExportStatus.Queued);
        work.GetLatestAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(item);
        (await workflow.GetLatestForUserAsync("synthetic-user", default))!.Status.Should().Be(DataExportStatus.Queued);
        await work.DidNotReceive().ClaimAsync(Arg.Any<StateWorkClaim>(), Arg.Any<CancellationToken>());
        artifacts.ReceivedCalls().Should().BeEmpty();
        tokens.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Existing_completed_download_keeps_authoritative_single_use_redemption_while_processing_paused()
    {
        var item = Item("completed");
        var work = Substitute.For<IStateWorkItemClient>();
        work.GetAsync(item.WorkItemId, Arg.Any<CancellationToken>()).Returns(item);
        var tokens = Substitute.For<IDataExportTokenProtector>();
        var capability = new DataExportCapability(item.WorkItemId, "synthetic-capability", "synthetic-hash");
        tokens.TryValidate("synthetic-capability", out Arg.Any<DataExportCapability>())
            .Returns(call => { call[1] = capability; return true; });
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        artifacts.CreateDownloadUrlAsync(item.ArtifactRef!, Arg.Any<TimeSpan>(), true, Arg.Any<CancellationToken>())
            .Returns(new PrivateArtifactDownload(new Uri("https://synthetic.invalid/download"), Now.AddDays(1)));
        var workflow = new StateDataExportWorkflow(work, artifacts, tokens, Paused(),
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(Now));
        (await workflow.RedeemDownloadAsync("synthetic-capability", default))!.Host.Should().Be("synthetic.invalid");
        await work.Received(1).ConsumeAsync(item.WorkItemId, Arg.Is<StateWorkConsume>(value =>
            value.DownloadTokenHash == capability.TokenHash && value.ExpectedVersion == item.Version), Arg.Any<CancellationToken>());
        await work.DidNotReceive().ClaimAsync(Arg.Any<StateWorkClaim>(), Arg.Any<CancellationToken>());
    }

    private static DurableWorkSweepExecutor Executor(IStateWorkItemClient work, IDurableWorkItemHandler handler,
        IOptions<DataExportOptions> options) => new(work, [handler], Options.Create(new DurableWorkExecutionOptions()),
            TimeProvider.System, NullLogger<DurableWorkSweepExecutor>.Instance, new DataExportProcessingPolicy(options));

    private static StateWorkItem Item(string status) => new()
    {
        WorkItemId = Guid.NewGuid(), Application = DurableWorkContract.Application,
        Kind = DurableWorkContract.DataExportKind, SubjectRef = DurableWorkContract.SubjectForUser("synthetic-user"),
        Status = status, Payload = JsonSerializer.SerializeToElement(new DataExportWorkPayload("synthetic-user", "json")),
        ArtifactRef = "synthetic-artifact", ArtifactExpiresAt = Now.AddDays(1),
        Version = 4, CreatedAt = Now, UpdatedAt = Now, DueAt = Now
    };
}
