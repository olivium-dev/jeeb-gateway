using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Cases;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Admin;

// gwdbx W1-04 — the one-shot admin_actions -> /v1/audit-events relay.
// Charter: idempotent (G-15 key = admin_actions.id), dry-run first, ships inert.
public class AdminAuditBackfillWorkerTests
{
    private static AdminAuditEntry Row(string action = "approve_kyc", string? entityId = null) => new()
    {
        Id = Guid.NewGuid().ToString(),
        AdminUserId = Guid.NewGuid().ToString(),
        Action = action,
        EntityType = "kyc_submission",
        EntityId = entityId ?? Guid.NewGuid().ToString(),
        AfterState = new Dictionary<string, object?> { ["status"] = "approved" },
        RequestId = "0HNNNMRGEMS0Q:00000001",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
    };

    [Fact]
    public async Task Relays_Every_Row_With_The_Local_Row_Id_As_The_Idempotency_Key()
    {
        var rows = new[] { Row(), Row("suspend_user") };
        var upstream = new RecordingOwnershipClient();

        var report = await RunAsync(rows, upstream, dryRun: false);

        report.Scanned.Should().Be(2);
        report.Accepted.Should().Be(2);
        report.Failed.Should().Be(0);
        report.Complete.Should().BeTrue();
        upstream.Calls.Select(c => c.IdempotencyKey).Should().BeEquivalentTo(rows.Select(r => r.Id),
            "G-15 — the backfill replays exactly the key the live mirror used");
    }

    [Fact]
    public async Task Re_Running_Sends_The_Same_Keys_Again_So_The_Pass_Is_Idempotent()
    {
        var rows = new[] { Row(), Row() };
        var upstream = new RecordingOwnershipClient();

        await RunAsync(rows, upstream, dryRun: false);
        var second = await RunAsync(rows, upstream, dryRun: false);

        second.Accepted.Should().Be(2, "a second pass is a replay upstream, never a duplicate insert");
        upstream.Calls.Should().HaveCount(4);
        upstream.Calls.Take(2).Select(c => c.IdempotencyKey)
            .Should().BeEquivalentTo(upstream.Calls.Skip(2).Select(c => c.IdempotencyKey),
                "identical keys are what makes uq(application, idempotency_key) absorb the re-run");
    }

    [Fact]
    public async Task DryRun_Enumerates_Everything_And_Posts_Nothing()
    {
        var upstream = new RecordingOwnershipClient();

        var report = await RunAsync(new[] { Row(), Row() }, upstream, dryRun: true);

        report.DryRun.Should().BeTrue();
        report.Scanned.Should().Be(2, "the rehearsal must still prove the read side works");
        report.Accepted.Should().Be(0);
        upstream.Calls.Should().BeEmpty("a dry run must never mutate the upstream audit trail");
    }

    [Fact]
    public async Task Body_Is_Byte_Identical_To_What_The_Live_Mirror_Would_Have_Sent()
    {
        // The upstream key is unique on (application, idempotency_key) and 409s a differing
        // body, so a drifting backfill body would be rejected rather than reconcile the gap.
        var row = Row();
        var upstream = new RecordingOwnershipClient();
        await RunAsync(new[] { row }, upstream, dryRun: false);

        var mirrored = AdminAuditEventMapping.ToAppendRequest(row, row.EntityId);

        var backfilled = upstream.Calls.Single().Body;
        Serialize(backfilled).Should().Be(Serialize(mirrored));
        backfilled.Application.Should().Be("jeeb-gateway");
        backfilled.ActorRole.Should().Be("admin");
        backfilled.OccurredAt.Should().Be(row.CreatedAt, "occurredAt is the ORIGINAL action time, not now");
    }

    [Fact]
    public async Task A_Row_With_No_Entity_Id_Still_Relays_Under_The_Required_Sentinel()
    {
        var row = Row();
        row = new AdminAuditEntry
        {
            Id = row.Id,
            AdminUserId = row.AdminUserId,
            Action = row.Action,
            EntityType = row.EntityType,
            EntityId = null,
            CreatedAt = row.CreatedAt,
        };
        var upstream = new RecordingOwnershipClient();

        var report = await RunAsync(new[] { row }, upstream, dryRun: false);

        report.Accepted.Should().Be(1, "resourceRef is required upstream; a NULL entity_id must not drop the event");
        upstream.Calls.Single().Body.ResourceRef.Should().Be("-");
    }

    [Fact]
    public async Task A_409_Counts_As_Present_Upstream_Not_As_A_Failure()
    {
        var upstream = new ConflictingOwnershipClient();

        var report = await RunAsync(new[] { Row() }, upstream, dryRun: false);

        report.Conflicted.Should().Be(1, "the live mirror already wrote this key — a conflict is not a gap");
        report.Failed.Should().Be(0);
        report.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task A_Failed_Relay_Is_Counted_And_Marks_The_Pass_Incomplete()
    {
        var upstream = new ThrowingOwnershipClient();

        var report = await RunAsync(new[] { Row(), Row() }, upstream, dryRun: false);

        report.Failed.Should().Be(2);
        report.Accepted.Should().Be(0);
        report.Complete.Should().BeFalse("a pass that dropped rows must never read as parity");
    }

    [Fact]
    public async Task Pages_Across_The_Batch_Boundary_Without_Skipping_Or_Duplicating()
    {
        var rows = Enumerable.Range(0, 7).Select(_ => Row()).ToArray();
        var upstream = new RecordingOwnershipClient();

        var report = await RunAsync(rows, upstream, dryRun: false, batchSize: 2);

        report.Scanned.Should().Be(7);
        upstream.Calls.Select(c => c.IdempotencyKey).Should().BeEquivalentTo(rows.Select(r => r.Id));
    }

    [Fact]
    public async Task Disarmed_Worker_Never_Touches_The_Database_Or_The_Upstream()
    {
        var source = new FakeSource(new[] { Row() });
        var upstream = new RecordingOwnershipClient();
        var worker = NewWorker(source, upstream, new AdminAuditBackfillOptions());

        await worker.StartAsync(default);
        await worker.StopAsync(default);

        worker.LastReport.Should().BeNull("Enabled defaults to false — the relay ships inert");
        source.Reads.Should().Be(0);
        upstream.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Armed_Worker_Runs_At_Startup_So_The_Disarmed_Zero_Above_Can_Fail()
    {
        // Known-positive control for the previous test: same wiring, flag on.
        var source = new FakeSource(new[] { Row() });
        var upstream = new RecordingOwnershipClient();
        var worker = NewWorker(source, upstream,
            new AdminAuditBackfillOptions { Enabled = true, DryRun = false });

        await worker.StartAsync(default);
        await worker.StopAsync(default);

        source.Reads.Should().BeGreaterThan(0);
        upstream.Calls.Should().ContainSingle();
        worker.LastReport!.Complete.Should().BeTrue();
    }

    [Fact]
    public async Task An_Exploding_Source_Is_Contained_And_Never_Faults_The_Host()
    {
        var worker = NewWorker(new ExplodingSource(), new RecordingOwnershipClient(),
            new AdminAuditBackfillOptions { Enabled = true, DryRun = false });

        var act = async () =>
        {
            await worker.StartAsync(default);
            await worker.StopAsync(default);
        };

        await act.Should().NotThrowAsync("a backfill that cannot run must not take the gateway down");
        worker.LastReport.Should().BeNull("nothing may be reported as relayed when the pass aborted");
    }

    [Fact]
    public void Defaults_Ship_Inert_And_Rehearse_Before_They_Write()
    {
        var shipped = new AdminAuditBackfillOptions();

        shipped.Enabled.Should().BeFalse("the relay is armed for a run, never by deploying it");
        shipped.DryRun.Should().BeTrue("arming alone must rehearse, not write");
        // G-22: AdminAuditMode owns the admin-audit cutover; these knobs must not shadow it.
        AdminAuditBackfillOptions.SectionName.Should().NotBe(GwdbxMigrationOptions.SectionName);
    }

    // ----- helpers -----------------------------------------------------------

    private static string Serialize(AuditEventAppendRequestV1 body) =>
        JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private static async Task<AdminAuditBackfillReport> RunAsync(
        IReadOnlyList<AdminAuditEntry> rows, IStateOwnershipClient upstream, bool dryRun, int batchSize = 200)
    {
        var options = new AdminAuditBackfillOptions { Enabled = true, DryRun = dryRun, BatchSize = batchSize };
        var source = new FakeSource(rows);
        return await NewWorker(source, upstream, options).RunOnceAsync(source, upstream, options, default);
    }

    private static AdminAuditBackfillWorker NewWorker(
        IAdminAuditBackfillSource source, IStateOwnershipClient upstream, AdminAuditBackfillOptions options)
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);
        services.AddSingleton(upstream);
        return new AdminAuditBackfillWorker(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options),
            NullLogger<AdminAuditBackfillWorker>.Instance);
    }

    /// <summary>Keyset-paged stand-in for admin_actions, ordered exactly as the SQL orders it.</summary>
    private sealed class FakeSource : IAdminAuditBackfillSource
    {
        private readonly List<AdminAuditEntry> _rows;

        public FakeSource(IEnumerable<AdminAuditEntry> rows) =>
            _rows = rows.OrderBy(r => r.CreatedAt).ThenBy(r => Guid.Parse(r.Id)).ToList();

        public int Reads { get; private set; }

        public Task<long> CountAsync(CancellationToken ct)
        {
            Reads++;
            return Task.FromResult((long)_rows.Count);
        }

        public Task<IReadOnlyList<AdminAuditEntry>> ReadPageAsync(
            AdminAuditCursor? after, int limit, CancellationToken ct)
        {
            Reads++;
            IEnumerable<AdminAuditEntry> query = _rows;
            if (after is { } cursor)
            {
                query = query.Where(r =>
                    (r.CreatedAt, Guid.Parse(r.Id)).CompareTo((cursor.CreatedAt, cursor.Id)) > 0);
            }

            return Task.FromResult<IReadOnlyList<AdminAuditEntry>>(query.Take(limit).ToList());
        }
    }

    private sealed class ExplodingSource : IAdminAuditBackfillSource
    {
        public Task<long> CountAsync(CancellationToken ct) =>
            throw new InvalidOperationException("GatewayPostgres is unreachable");

        public Task<IReadOnlyList<AdminAuditEntry>> ReadPageAsync(
            AdminAuditCursor? after, int limit, CancellationToken ct) =>
            throw new InvalidOperationException("GatewayPostgres is unreachable");
    }

    private sealed record RelayCall(AuditEventAppendRequestV1 Body, string IdempotencyKey);

    private class RecordingOwnershipClient : NotSupportedOwnershipClient
    {
        private readonly ConcurrentQueue<RelayCall> _calls = new();

        public IReadOnlyList<RelayCall> Calls => _calls.ToList();

        public override Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            _calls.Enqueue(new RelayCall(body, idempotencyKey));
            return Task.FromResult(new AuditEventRecordV1 { EventId = Guid.NewGuid() });
        }
    }

    private sealed class ConflictingOwnershipClient : NotSupportedOwnershipClient
    {
        public override Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new GenericCaseApiException(409, "audit.idempotency_conflict");
    }

    private sealed class ThrowingOwnershipClient : NotSupportedOwnershipClient
    {
        public override Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new GenericCaseApiException(503, "state-service is down");
    }

    private abstract class NotSupportedOwnershipClient : IStateOwnershipClient
    {
        public virtual Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
