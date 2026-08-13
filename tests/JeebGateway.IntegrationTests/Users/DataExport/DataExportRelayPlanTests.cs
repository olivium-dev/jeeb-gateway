using System.Text.Json;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Users.DataExport;

// gwdbx W1-07 — the OPEN-only relay contract: which rows travel, which stay archive-only,
// and the G-20 property that no BYTEA / token / failure text can ride along.
public class DataExportRelayPlanTests
{
    private const string UserId = "u-export-relay-1";

    private static readonly DateTimeOffset DueBy = new(2026, 8, 14, 12, 7, 34, TimeSpan.Zero);

    private static DataExportRelayPlan.RelayRow OpenRow(
        string status = DataExportStatus.Ready, string userId = UserId) =>
        new("e1c2f2b0-0000-4000-8000-000000000001", userId, status, DataExportFormat.Json, DueBy);

    // ----- which rows travel -------------------------------------------------

    [Fact]
    public void Relay_Statuses_Are_Exactly_The_Open_States()
    {
        DataExportRelayPlan.RelayStatuses.Should().BeEquivalentTo(
            new[] { "queued", "processing", "ready" },
            "the charter relays OPEN rows only, and these are the three uq_data_exports_user_open covers");
        DataExportRelayPlan.RelayStatuses.Should().BeEquivalentTo(DataExportStatus.OpenStates);
    }

    [Theory]
    [InlineData(DataExportStatus.Queued)]
    [InlineData(DataExportStatus.Processing)]
    [InlineData(DataExportStatus.Ready)]
    public void Open_Rows_Are_Relayed(string status) =>
        DataExportRelayPlan.ShouldRelay(status).Should().BeTrue();

    [Theory]
    [InlineData(DataExportStatus.Delivered)]
    [InlineData(DataExportStatus.Expired)]
    [InlineData(DataExportStatus.Failed)]
    public void Terminal_Rows_Are_Archive_Only(string status)
    {
        DataExportRelayPlan.ShouldRelay(status).Should().BeFalse();

        var build = () => DataExportRelayPlan.BuildWorkItem(OpenRow(status));
        build.Should().Throw<InvalidOperationException>(
            "a terminal row must never be able to reach the upstream create leg")
            .WithMessage("*terminal*");
    }

    // ----- G-20: PII cannot ride along --------------------------------------

    [Theory]
    [InlineData("payload")]
    [InlineData("download_token")]
    [InlineData("failure_reason")]
    [InlineData("*")]
    public void Select_Never_Reads_A_Pii_Column(string forbidden) =>
        DataExportRelayPlan.SelectOpenSql.Should().NotContain(forbidden,
            "the relay reads an explicit narrow column list so BYTEA PII cannot be copied");

    [Fact]
    public void Work_Item_Payload_Carries_Only_ExportId_And_Format()
    {
        var row = OpenRow();

        var payload = DataExportRelayPlan.BuildWorkItem(row).Payload!.Value;

        payload.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("exportId", "format");
        payload.GetProperty("exportId").GetString().Should().Be(row.ExportId);
        payload.GetProperty("format").GetString().Should().Be(DataExportFormat.Json);
    }

    // ----- idempotency: the relay replays the mirror's own call --------------

    [Fact]
    public void Idempotency_Key_Is_Byte_Exact()
    {
        DataExportRelayPlan.IdempotencyKeyFor(OpenRow()).Should().Be("data-export:" + UserId);
        DataExportRelayPlan.IdempotencyKeyFor(OpenRow())
            .Should().Be(MirroringDataExportStore.IdempotencyKeyFor(UserId));
    }

    [Fact]
    public async Task Relay_Body_Matches_The_Body_The_Live_Mirror_Would_Have_Sent()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewMirroringStore(upstream);

        var mirrored = await store.RequestAsync(UserId, DataExportFormat.Json, default);
        var relayed = DataExportRelayPlan.BuildWorkItem(new DataExportRelayPlan.RelayRow(
            mirrored.Id, mirrored.UserId, mirrored.Status, mirrored.Format, mirrored.DueBy));

        upstream.Creates.Should().ContainSingle();
        var sent = upstream.Creates[0];
        sent.Key.Should().Be(DataExportRelayPlan.IdempotencyKeyFor(
            new DataExportRelayPlan.RelayRow(
                mirrored.Id, mirrored.UserId, mirrored.Status, mirrored.Format, mirrored.DueBy)));
        sent.Body.Application.Should().Be(relayed.Application);
        sent.Body.Kind.Should().Be(relayed.Kind);
        sent.Body.SubjectRef.Should().Be(relayed.SubjectRef);
        sent.Body.DueAt.Should().Be(relayed.DueAt);
        sent.Body.Payload!.Value.GetRawText().Should().Be(relayed.Payload!.Value.GetRawText(),
            "the relay is a replay of the create the mirror never got to make, not a second producer");
    }

    // ----- the shared key can only ever cover one open row per user ----------

    [Fact]
    public void One_Open_Row_Per_User_Passes()
    {
        var rows = new[] { OpenRow(userId: "u-1"), OpenRow(userId: "u-2") };

        var assert = () => DataExportRelayPlan.AssertOneOpenRowPerUser(rows);

        assert.Should().NotThrow();
    }

    [Fact]
    public void Two_Open_Rows_For_One_User_Abort_The_Run()
    {
        var rows = new[] { OpenRow(DataExportStatus.Queued), OpenRow(DataExportStatus.Ready) };

        var assert = () => DataExportRelayPlan.AssertOneOpenRowPerUser(rows);

        assert.Should().Throw<InvalidOperationException>(
            "one shared key cannot carry two rows — relaying only one would silently drop the other")
            .WithMessage("*uq_data_exports_user_open*");
    }

    // ----- helpers -----------------------------------------------------------

    private static MirroringDataExportStore NewMirroringStore(IStateOwnershipClient upstream)
    {
        var options = Options.Create(new DataExportOptions
        {
            Sla = TimeSpan.FromHours(72),
            LinkValidity = TimeSpan.FromDays(7),
        });
        var services = new ServiceCollection();
        services.AddSingleton(upstream);

        return new MirroringDataExportStore(
            new InMemoryDataExportStore(TimeProvider.System, options),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor(new GwdbxMigrationOptions
            {
                DataExportMode = "dual-write-local-read",
            }),
            NullLogger<MirroringDataExportStore>.Instance);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<GwdbxMigrationOptions>
    {
        public StaticOptionsMonitor(GwdbxMigrationOptions value) => CurrentValue = value;

        public GwdbxMigrationOptions CurrentValue { get; }

        public GwdbxMigrationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GwdbxMigrationOptions, string?> listener) => null;
    }

    private sealed class RecordingOwnershipClient : IStateOwnershipClient
    {
        public List<(WorkItemCreateRequestV1 Body, string Key)> Creates { get; } = new();

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Creates.Add((body, idempotencyKey));
            return Task.FromResult(new WorkItemRecordV1
            {
                WorkItemId = Guid.NewGuid(),
                Application = body.Application,
                Kind = body.Kind,
                SubjectRef = body.SubjectRef,
                Status = "queued",
            });
        }

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) =>
            Task.FromResult<WorkItemRecordV1?>(null);

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
