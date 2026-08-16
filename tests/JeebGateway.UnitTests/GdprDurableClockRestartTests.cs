using FluentAssertions;
using JeebGateway.Artifacts;
using JeebGateway.Jobs;
using JeebGateway.Requests;
using JeebGateway.StateService.Work;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

/// <summary>
/// The GDPR purge clock and the data-export SLA used to live in process memory, so a gateway
/// restart silently dropped a pending erasure and every queued export. These tests simulate the
/// restart literally: the gateway-side objects are thrown away and rebuilt while only the durable
/// substrate survives. Each survival test is paired with a loss-injection control that rebuilds
/// against an EMPTY substrate and asserts the work is lost — if the survival assertion could pass
/// without durability, that control would pass too.
/// </summary>
public class GdprDurableClockRestartTests
{
    private const string UserId = "user-gdpr-1";
    private static readonly DateTimeOffset T0 = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------------
    // Account deletion — the 30-day purge deadline
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Pending_purge_survives_a_gateway_restart_and_executes_when_the_30_day_clock_expires()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        // ---- gateway instance A: the user asks for erasure, then the process dies ----
        var requested = await new StateAccountDeletionWorkflow(durable)
            .RequestAsync(UserId, hasActiveDelivery: false, default);
        requested.Status.Should().Be(AccountDeletionStatus.Scheduled);
        requested.ScheduledPurgeAt.Should().Be(T0 + AccountDeletionPolicy.PurgeDelay);

        // ---- gateway instance B: brand-new collaborators, nothing carried over ----
        var restarted = NewDeletionGateway(durable, clock);

        // First sweep records the deadline on the durable item; nothing is purged yet.
        await restarted.Sweep(DurableWorkContract.AccountDeletionKind);
        await restarted.Users.DidNotReceive().PurgePiiAsync(UserId, Arg.Any<CancellationToken>());

        // The legal clock expires while the gateway that took the request no longer exists.
        clock.Advance(AccountDeletionPolicy.PurgeDelay + TimeSpan.FromDays(1));
        await restarted.Sweep(DurableWorkContract.AccountDeletionKind);

        await restarted.Users.Received(1).PurgePiiAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Control_purge_is_LOST_when_the_substrate_does_not_survive_the_restart()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        await new StateAccountDeletionWorkflow(durable)
            .RequestAsync(UserId, hasActiveDelivery: false, default);

        // Injected loss: the restart also wipes the substrate — exactly the pre-fix behaviour,
        // where the only record of the erasure was a dictionary inside the dead process.
        var lostSubstrate = new FakeDurableWorkStore(clock);
        var restarted = NewDeletionGateway(lostSubstrate, clock);

        clock.Advance(AccountDeletionPolicy.PurgeDelay + TimeSpan.FromDays(1));
        await restarted.Sweep(DurableWorkContract.AccountDeletionKind);

        await restarted.Users.DidNotReceive().PurgePiiAsync(UserId, Arg.Any<CancellationToken>());
        lostSubstrate.Count.Should().Be(0, "the control must genuinely have nothing to recover");
    }

    [Fact]
    public async Task Purge_waits_for_an_active_delivery_across_restarts_without_burning_the_retry_budget()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        await new StateAccountDeletionWorkflow(durable)
            .RequestAsync(UserId, hasActiveDelivery: true, default);

        // A delivery stays in flight across many sweeps and many restarts.
        for (var i = 0; i < 12; i++)
        {
            var instance = NewDeletionGateway(durable, clock, activeDeliveries: 1);
            await instance.Sweep(DurableWorkContract.AccountDeletionKind);
            await instance.Users.DidNotReceive().PurgePiiAsync(UserId, Arg.Any<CancellationToken>());
            clock.Advance(TimeSpan.FromHours(6));
        }

        // Deferral refunds the claim's attempt, so the erasure is still claimable — a purge that
        // waited out a long delivery must not be terminalised by an exhausted attempt budget.
        var status = await new StateAccountDeletionWorkflow(durable)
            .GetLatestForUserAsync(UserId, default);
        status!.Status.Should().Be(AccountDeletionStatus.PendingActiveDelivery);

        // The delivery finally clears; the erasure proceeds on its own 30-day clock.
        var final = NewDeletionGateway(durable, clock, activeDeliveries: 0);
        await final.Sweep(DurableWorkContract.AccountDeletionKind);
        clock.Advance(AccountDeletionPolicy.PurgeDelay + TimeSpan.FromDays(1));
        await final.Sweep(DurableWorkContract.AccountDeletionKind);

        await final.Users.Received(1).PurgePiiAsync(UserId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_delete_request_after_a_restart_reuses_the_open_erasure_and_its_original_deadline()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        var first = await new StateAccountDeletionWorkflow(durable)
            .RequestAsync(UserId, hasActiveDelivery: false, default);

        clock.Advance(TimeSpan.FromDays(5));

        // A restarted gateway must not restart the legal clock on a retried request.
        var second = await new StateAccountDeletionWorkflow(durable)
            .RequestAsync(UserId, hasActiveDelivery: false, default);

        second.RequestedAt.Should().Be(first.RequestedAt);
        second.ScheduledPurgeAt.Should().Be(first.ScheduledPurgeAt);
        durable.Count.Should().Be(1, "a retry must not open a second erasure");
    }

    // ---------------------------------------------------------------------
    // Data export — the 72-hour SLA and the queued job
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Queued_export_survives_a_gateway_restart_and_is_packaged_after_it()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        var queued = await NewExportWorkflow(durable, clock)
            .RequestAsync(UserId, DataExportFormat.Json, default);
        queued.Status.Should().Be(DataExportStatus.Queued);
        queued.DueBy.Should().Be(T0 + TimeSpan.FromHours(72));

        // ---- restart: fresh packager, fresh notifier, fresh artifact owner ----
        var restarted = NewExportGateway(durable, clock);
        await restarted.Sweep(DurableWorkContract.DataExportKind);

        await restarted.Packager.Received(1).BuildAsync(
            UserId, DataExportFormat.Json, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await restarted.Notifier.Received(1).NotifyReadyAsync(
            UserId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());

        var after = await NewExportWorkflow(durable, clock).GetLatestForUserAsync(UserId, default);
        after!.Status.Should().Be(DataExportStatus.Ready);
        after.DownloadToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Control_queued_export_is_LOST_when_the_substrate_does_not_survive_the_restart()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        await NewExportWorkflow(durable, clock).RequestAsync(UserId, DataExportFormat.Json, default);

        var lostSubstrate = new FakeDurableWorkStore(clock);
        var restarted = NewExportGateway(lostSubstrate, clock);
        await restarted.Sweep(DurableWorkContract.DataExportKind);

        await restarted.Packager.DidNotReceive().BuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        (await NewExportWorkflow(lostSubstrate, clock).GetLatestForUserAsync(UserId, default))
            .Should().BeNull("the control must genuinely have nothing to recover");
    }

    [Fact]
    public async Task Download_token_minted_before_a_restart_still_redeems_after_it()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        await NewExportWorkflow(durable, clock).RequestAsync(UserId, DataExportFormat.Json, default);
        var producing = NewExportGateway(durable, clock);
        await producing.Sweep(DurableWorkContract.DataExportKind);

        var ready = await NewExportWorkflow(durable, clock).GetLatestForUserAsync(UserId, default);
        var token = ready!.DownloadToken!;

        // The capability token is derived from the durable work id, so a gateway that never saw
        // the export can still validate and atomically consume it.
        var afterRestart = NewExportWorkflow(durable, clock);
        var url = await afterRestart.RedeemDownloadAsync(token, default);
        url.Should().NotBeNull();

        // Single use is enforced by the durable consume, not by a process-local flag.
        (await NewExportWorkflow(durable, clock).RedeemDownloadAsync(token, default))
            .Should().BeNull("the capability is consumed exactly once");
    }

    [Fact]
    public async Task Export_that_blew_its_72_hour_sla_while_the_gateway_was_down_fails_visibly()
    {
        var clock = new FakeTimeProvider(T0);
        var durable = new FakeDurableWorkStore(clock);

        await NewExportWorkflow(durable, clock).RequestAsync(UserId, DataExportFormat.Json, default);

        // The gateway is down past the contractual deadline.
        clock.Advance(TimeSpan.FromHours(80));
        var restarted = NewExportGateway(durable, clock);
        await restarted.Sweep(DurableWorkContract.DataExportKind);

        await restarted.Packager.DidNotReceive().BuildAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        var after = await NewExportWorkflow(durable, clock).GetLatestForUserAsync(UserId, default);
        after!.Status.Should().Be(DataExportStatus.Failed,
            "a missed legal deadline must surface, not silently disappear");
    }

    // ---------------------------------------------------------------------
    // harness
    // ---------------------------------------------------------------------

    private static DeletionGateway NewDeletionGateway(
        IStateWorkItemClient durable, TimeProvider clock, int activeDeliveries = 0)
    {
        var users = Substitute.For<IUsersStore>();
        users.PurgePiiAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        var requests = Substitute.For<IRequestsStore>();
        requests.CountActiveForClientAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(activeDeliveries);
        requests.AnonymizeForClientAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var tokens = Substitute.For<ITokenService>();
        tokens.RevokeAllForUserAsync(Arg.Any<string>(), Arg.Any<RevocationReason>(), Arg.Any<CancellationToken>())
            .Returns(1);
        var ledger = Substitute.For<IFinancialLedgerAnonymizer>();
        ledger.AnonymizeForUserAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var handler = new AccountDeletionWorkHandler(
            users, requests, tokens, ledger, clock,
            Options.Create(new AccountDeletionExecutionOptions()));
        return new DeletionGateway(NewExecutor(durable, clock, handler), users);
    }

    private static ExportGateway NewExportGateway(IStateWorkItemClient durable, TimeProvider clock)
    {
        var packager = Substitute.For<IDataExportPackager>();
        packager.BuildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(new DataExportPayload
            {
                Bytes = [1, 2, 3],
                ContentType = "application/json",
                FileName = "export.json",
            });
        var notifier = Substitute.For<IDataExportNotifier>();
        var artifacts = NewArtifactStore(clock);

        var handler = new DataExportWorkHandler(
            packager, notifier, artifacts, NewTokenProtector(),
            Options.Create(new DataExportOptions()), clock);
        return new ExportGateway(NewExecutor(durable, clock, handler), packager, notifier);
    }

    private static DurableWorkSweepExecutor NewExecutor(
        IStateWorkItemClient durable, TimeProvider clock, IDurableWorkItemHandler handler) =>
        new(durable, [handler],
            Options.Create(new DurableWorkExecutionOptions()),
            clock,
            NullLogger<DurableWorkSweepExecutor>.Instance);

    private static StateDataExportWorkflow NewExportWorkflow(IStateWorkItemClient durable, TimeProvider clock) =>
        new(durable, NewArtifactStore(clock), NewTokenProtector(),
            Options.Create(new DataExportOptions()), clock);

    private static IPrivateArtifactStore NewArtifactStore(TimeProvider clock)
    {
        var artifacts = Substitute.For<IPrivateArtifactStore>();
        artifacts.RecoverUploadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((PrivateArtifact?)null);
        artifacts.PutAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<byte[]>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(call => new PrivateArtifact("artifact-ref", call.ArgAt<DateTimeOffset>(5), 3));
        artifacts.CreateDownloadUrlAsync(
                Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new PrivateArtifactDownload(
                new Uri("https://artifacts.invalid/signed"), clock.GetUtcNow().AddMinutes(5)));
        return artifacts;
    }

    // One mounted key file for the whole class: the signing key must be identical across
    // "restarts", otherwise a token minted before one could never validate after it.
    private static readonly string SigningKeyPath = WriteSigningKey();

    private static string WriteSigningKey()
    {
        var path = Path.Combine(Path.GetTempPath(), $"jeeb-export-key-{Guid.NewGuid():N}.key");
        File.WriteAllText(path, "0123456789abcdef0123456789abcdef0123456789abcdef");
        return path;
    }

    private static IDataExportTokenProtector NewTokenProtector() =>
        new DataExportTokenProtector(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DataExportTokenProtector.SigningKeyFileKey] = SigningKeyPath,
            })
            .Build());

    private sealed record DeletionGateway(DurableWorkSweepExecutor Executor, IUsersStore Users)
    {
        public Task Sweep(string kind) => Executor.SweepAsync(kind, null, default);
    }

    private sealed record ExportGateway(
        DurableWorkSweepExecutor Executor, IDataExportPackager Packager, IDataExportNotifier Notifier)
    {
        public Task Sweep(string kind) => Executor.SweepAsync(kind, null, default);
    }
}
