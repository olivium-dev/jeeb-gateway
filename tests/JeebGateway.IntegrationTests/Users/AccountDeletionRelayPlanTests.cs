using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using JeebGateway.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Users;

// gwdbx W3-07 prep — the OPEN-only account_deletions relay contract: which rows travel, which
// stay archive-only, and that the relay replays the live mirror's exact call.
public class AccountDeletionRelayPlanTests
{
    private const string UserId = "7d3f2a10-0000-4000-8000-000000000001";

    private static readonly DateTimeOffset PurgeAt = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);

    private static AccountDeletionRelayPlan.RelayRow OpenRow(
        string status = AccountDeletionStatus.Scheduled, string userId = UserId) =>
        new(userId, status, "hash-" + userId, status == AccountDeletionStatus.Scheduled ? PurgeAt : null);

    // ----- which rows travel -------------------------------------------------

    [Fact]
    public void Relay_Statuses_Are_Exactly_The_Open_States()
    {
        AccountDeletionRelayPlan.RelayStatuses.Should().BeEquivalentTo(
            new[] { "pending_active_delivery", "scheduled" },
            "completed rows already purged their PII, so an upstream item would be born dead");
    }

    [Theory]
    [InlineData(AccountDeletionStatus.PendingActiveDelivery)]
    [InlineData(AccountDeletionStatus.Scheduled)]
    public void Open_Rows_Are_Relayed(string status) =>
        AccountDeletionRelayPlan.ShouldRelay(status).Should().BeTrue();

    [Fact]
    public void Completed_Rows_Are_Archive_Only()
    {
        AccountDeletionRelayPlan.ShouldRelay(AccountDeletionStatus.Completed).Should().BeFalse();

        var build = () => AccountDeletionRelayPlan.BuildWorkItem(OpenRow(AccountDeletionStatus.Completed));
        build.Should().Throw<InvalidOperationException>(
            "a terminal row must never be able to reach the upstream create leg")
            .WithMessage("*terminal*");
    }

    // ----- narrow read: lifecycle bookkeeping never travels ------------------

    [Theory]
    [InlineData("completed_at")]
    [InlineData("side_effects")]
    [InlineData("*")]
    public void Select_Reads_Only_The_Narrow_Column_List(string forbidden) =>
        AccountDeletionRelayPlan.SelectOpenSql.Should().NotContain(forbidden,
            "the relay reads an explicit narrow column list, mirroring the W1-07 G-20 discipline");

    [Fact]
    public void Work_Item_Payload_Carries_Only_Status_And_Hash()
    {
        var row = OpenRow();

        var payload = AccountDeletionRelayPlan.BuildWorkItem(row).Payload!.Value;

        payload.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo("status", "anonymizedUserHash");
        payload.GetProperty("status").GetString().Should().Be(AccountDeletionStatus.Scheduled);
        payload.GetProperty("anonymizedUserHash").GetString().Should().Be(row.AnonymizedUserHash);
    }

    [Fact]
    public void DueAt_Is_The_Purge_Deadline_And_Null_While_Deliveries_Hold_The_Clock()
    {
        AccountDeletionRelayPlan.BuildWorkItem(OpenRow()).DueAt.Should().Be(PurgeAt);
        AccountDeletionRelayPlan.BuildWorkItem(OpenRow(AccountDeletionStatus.PendingActiveDelivery))
            .DueAt.Should().BeNull();
    }

    // ----- idempotency: the relay replays the mirror's own call --------------

    [Fact]
    public void Idempotency_Key_Is_Byte_Exact()
    {
        AccountDeletionRelayPlan.IdempotencyKeyFor(OpenRow()).Should().Be("account-deletion:" + UserId);
        AccountDeletionRelayPlan.IdempotencyKeyFor(OpenRow())
            .Should().Be(StateServiceAccountDeletionStore.IdempotencyKeyFor(UserId));
    }

    [Fact]
    public async Task Relay_Body_Matches_The_Body_The_Live_Mirror_Would_Have_Sent()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewMirroringStore(upstream);

        var mirrored = await store.RequestAsync(UserId, hasActiveDelivery: false, default);
        var relayed = AccountDeletionRelayPlan.BuildWorkItem(new AccountDeletionRelayPlan.RelayRow(
            mirrored.UserId, mirrored.Status, mirrored.AnonymizedUserHash, mirrored.ScheduledPurgeAt));

        upstream.Creates.Should().ContainSingle();
        var (key, body) = upstream.Creates[0];
        key.Should().Be(AccountDeletionRelayPlan.IdempotencyKeyFor(new AccountDeletionRelayPlan.RelayRow(
            mirrored.UserId, mirrored.Status, mirrored.AnonymizedUserHash, mirrored.ScheduledPurgeAt)));
        body.Application.Should().Be(relayed.Application);
        body.Kind.Should().Be(relayed.Kind);
        body.SubjectRef.Should().Be(relayed.SubjectRef);
        body.DueAt.Should().Be(relayed.DueAt);
        body.Payload!.Value.GetRawText().Should().Be(relayed.Payload!.Value.GetRawText(),
            "the relay is a replay of the create the mirror never got to make, not a second producer");
    }

    // ----- helpers -----------------------------------------------------------

    private static StateServiceAccountDeletionStore NewMirroringStore(IStateOwnershipClient upstream)
    {
        var services = new ServiceCollection();
        services.AddSingleton(upstream);

        return new StateServiceAccountDeletionStore(
            new FakeDeletionStore(),
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<GwdbxMigrationOptions>(
                new GwdbxMigrationOptions { AccountDeletionMode = "dual-write-local-read" }),
            NullLogger<StateServiceAccountDeletionStore>.Instance);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Stands in for the durable gateway-local chain; the record it mints is the authority.
    private sealed class FakeDeletionStore : IAccountDeletionStore
    {
        private readonly ConcurrentDictionary<string, AccountDeletionRequest> _records =
            new(StringComparer.Ordinal);

        public Task<AccountDeletionRequest> RequestAsync(
            string userId, bool hasActiveDelivery, CancellationToken ct) =>
            Task.FromResult(_records.GetOrAdd(userId, _ => new AccountDeletionRequest
            {
                UserId = userId,
                Status = hasActiveDelivery
                    ? AccountDeletionStatus.PendingActiveDelivery
                    : AccountDeletionStatus.Scheduled,
                RequestedAt = PurgeAt - TimeSpan.FromDays(30),
                ScheduledPurgeAt = hasActiveDelivery ? null : PurgeAt,
                AnonymizedUserHash = "hash-" + userId,
            }));

        public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct) =>
            Task.FromResult(_records.TryGetValue(userId, out var record) ? record : null);

        public Task AdvanceAsync(DateTimeOffset now, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class RecordingOwnershipClient : IStateOwnershipClient
    {
        public List<(string IdempotencyKey, WorkItemCreateRequestV1 Body)> Creates { get; } = new();

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Creates.Add((idempotencyKey, body));
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
            WorkClaimRequestV1 body, CancellationToken ct) => throw new NotSupportedException();

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
