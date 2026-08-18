using System.Text.Json;
using FluentAssertions;
using JeebGateway.Artifacts;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class DataExportDisabledContractTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Disabled_workflow_refuses_every_surface_before_state_or_artifact_calls()
    {
        var work = Substitute.For<IStateWorkItemClient>();
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        var tokens = Substitute.For<IDataExportTokenProtector>();
        var workflow = new StateDataExportWorkflow(
            work,
            artifacts,
            tokens,
            DisabledOptions(),
            new FakeTimeProvider(Now));

        await AssertDisabled(() => workflow.RequestAsync("user-1", DataExportFormat.Json, default));
        await AssertDisabled(() => workflow.GetLatestForUserAsync("user-1", default));
        await AssertDisabled(() => workflow.RedeemDownloadAsync("opaque-token", default));

        work.ReceivedCalls().Should().BeEmpty();
        artifacts.ReceivedCalls().Should().BeEmpty();
        tokens.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_durable_handler_defers_before_packaging_artifact_or_notification_calls()
    {
        var packager = Substitute.For<IDataExportPackager>();
        var notifier = Substitute.For<IDataExportNotifier>();
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        var tokens = Substitute.For<IDataExportTokenProtector>();
        var handler = new DataExportWorkHandler(
            packager,
            notifier,
            artifacts,
            tokens,
            DisabledOptions(),
            new FakeTimeProvider(Now));
        var item = new StateWorkItem
        {
            WorkItemId = Guid.NewGuid(),
            Application = DurableWorkContract.Application,
            Kind = DurableWorkContract.DataExportKind,
            SubjectRef = "sha256:disabled",
            Status = "leased",
            Payload = JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-1", DataExportFormat.Json)),
            CreatedAt = Now,
            UpdatedAt = Now,
        };

        var result = await handler.ExecuteAsync(item, default);

        result.Outcome.Should().Be(DurableWorkOutcome.Defer);
        result.Error.Should().Contain("disabled in this environment");
        packager.ReceivedCalls().Should().BeEmpty();
        notifier.ReceivedCalls().Should().BeEmpty();
        artifacts.ReceivedCalls().Should().BeEmpty();
        tokens.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_legacy_processor_returns_before_resolving_any_owner()
    {
        var services = Substitute.For<IServiceProvider>();
        var processor = new DataExportProcessor(
            services,
            new FakeTimeProvider(Now),
            DisabledOptions(),
            NullLogger<DataExportProcessor>.Instance);

        (await processor.ProcessOnceAsync(default)).Should().Be(0);

        services.ReceivedCalls().Should().BeEmpty();
    }

    private static IOptions<DataExportOptions> DisabledOptions() =>
        Options.Create(new DataExportOptions { Enabled = false });

    private static async Task AssertDisabled(Func<Task> action) =>
        await action.Should().ThrowAsync<DataExportDisabledException>()
            .WithMessage("*disabled in this environment*");
}
