using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Migration;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Ownership;
using JeebGateway.Users;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Users;

// gwdbx W3-05 — StateServiceAccountDeletionStore dual-write over /work-items. Charter cases:
// local record stays authoritative, upstream failure never fails the delete, key, default rung.
public class StateServiceAccountDeletionStoreTests
{
    private const string UserId = "u-del-1";

    // ----- ladder default -----------------------------------------------------

    [Fact]
    public void AccountDeletionMode_Defaults_To_Local()
    {
        var options = new GwdbxMigrationOptions();

        options.AccountDeletionMode.Should().Be("local",
            "A10 — W3-05 lands the rail dormant; the flip to dual-write is W3-11");
        options.AccountDeletion.Should().Be(GwdbxMigrationPhase.Local);
    }

    [Fact]
    public async Task Local_Mode_Does_Not_Mirror_But_Still_Records_Locally()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewStore(upstream, "local", out var inner);

        var record = await store.RequestAsync(UserId, hasActiveDelivery: false, default);

        upstream.Creates.Should().BeEmpty("the ladder still sits at 'local'; the flip is W3-11");
        record.Status.Should().Be(AccountDeletionStatus.Scheduled);
        (await inner.GetAsync(UserId, default))!.RequestedAt.Should().Be(record.RequestedAt);
    }

    // ----- dual-write: create leg --------------------------------------------

    [Fact]
    public async Task DualWriteLocalRead_Creates_The_Work_Item_With_The_Charter_Idempotency_Key()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _);

        await store.RequestAsync(UserId, hasActiveDelivery: false, default);

        upstream.Creates.Should().ContainSingle();
        upstream.Creates[0].IdempotencyKey.Should().Be("account-deletion:" + UserId,
            "G-15 — the mirror key is exactly account-deletion:<userId>, one open deletion per user");
        upstream.Creates[0].Body.Application.Should().Be("jeeb-gateway");
        upstream.Creates[0].Body.Kind.Should().Be("account-deletion");
        upstream.Creates[0].Body.SubjectRef.Should().Be(UserId);
    }

    [Fact]
    public async Task Work_Item_DueAt_Is_The_30_Day_Purge_Deadline_And_Null_While_Pending()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _);

        var scheduled = await store.RequestAsync(UserId, hasActiveDelivery: false, default);
        var pending = await store.RequestAsync("u-del-2", hasActiveDelivery: true, default);

        upstream.Creates[0].Body.DueAt.Should().Be(scheduled.ScheduledPurgeAt);
        (scheduled.ScheduledPurgeAt - scheduled.RequestedAt).Should().Be(TimeSpan.FromDays(30));
        pending.Status.Should().Be(AccountDeletionStatus.PendingActiveDelivery);
        upstream.Creates[1].Body.DueAt.Should().BeNull(
            "an active delivery holds the purge clock, so no deadline is mirrored yet");
    }

    [Fact]
    public async Task Work_Item_Payload_Carries_No_Raw_Pii()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out _);

        await store.RequestAsync(UserId, hasActiveDelivery: false, default);

        var payload = upstream.Creates[0].Body.Payload!.Value;
        payload.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "status", "anonymizedUserHash" });
        payload.GetRawText().Should().NotContain(UserId, "only the pseudonym travels in the payload");
    }

    // ----- fail-open ---------------------------------------------------------

    [Fact]
    public async Task Upstream_Failure_Never_Fails_The_Deletion_Request()
    {
        var upstream = new ThrowingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out var inner);

        var act = async () => await store.RequestAsync(UserId, hasActiveDelivery: false, default);

        var record = (await act.Should().NotThrowAsync("the mirror is fail-open")).Subject;
        upstream.Attempts.Should().Be(1, "the probe really reached the throwing upstream");
        record.Status.Should().Be(AccountDeletionStatus.Scheduled);
        (await inner.GetAsync(UserId, default)).Should().NotBeNull(
            "the GDPR request survives a dead state-service — the local record is authoritative");
    }

    // ----- local authority ---------------------------------------------------

    [Fact]
    public async Task Reads_And_The_State_Machine_Stay_Local()
    {
        var upstream = new RecordingOwnershipClient();
        var store = NewStore(upstream, "dual-write-local-read", out var inner);
        await store.RequestAsync(UserId, hasActiveDelivery: false, default);

        var read = await store.GetAsync(UserId, default);
        await store.AdvanceAsync(DateTimeOffset.UtcNow, default);

        read!.UserId.Should().Be(UserId);
        upstream.Reads.Should().Be(0, "at dual-write-local-read the local record answers every read");
        inner.Advances.Should().Be(1, "the purge state machine is driven on the inner store");
    }

    // ----- live composition --------------------------------------------------

    // W5-11 deleted StoreDurabilityGuard's catalog; the surviving owner of "which store a boot
    // resolves" is the Program.cs registration, so the claim is made against the container.
    [Fact]
    public void A_Booted_Host_Resolves_IAccountDeletionStore_To_The_StateService_Decorator()
    {
        // The FRAMEWORK factory, not this suite's shadowing one, which swaps in test owners.
        using var host = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program>();

        host.Services.GetRequiredService<IAccountDeletionStore>()
            .Should().BeOfType<StateServiceAccountDeletionStore>(
                "the dual-write cases above only describe production if the decorator is what a boot gets");
    }

    // ----- helpers -----------------------------------------------------------

    private static StateServiceAccountDeletionStore NewStore(
        IStateOwnershipClient upstream, string mode, out RecordingDeletionStore inner)
    {
        inner = new RecordingDeletionStore();
        var services = new ServiceCollection();
        services.AddSingleton(upstream);

        return new StateServiceAccountDeletionStore(
            inner,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new StaticOptionsMonitor<GwdbxMigrationOptions>(
                new GwdbxMigrationOptions { AccountDeletionMode = mode }),
            NullLogger<StateServiceAccountDeletionStore>.Instance);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        public StaticOptionsMonitor(T value) => CurrentValue = value;

        public T CurrentValue { get; }

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    // Stands in for the durable gateway-local chain: the record it holds is the authority.
    private sealed class RecordingDeletionStore : IAccountDeletionStore
    {
        private readonly ConcurrentDictionary<string, AccountDeletionRequest> _records = new(StringComparer.Ordinal);

        public int Advances { get; private set; }

        public Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
        {
            var now = DateTimeOffset.UtcNow;
            var record = _records.GetOrAdd(userId, _ => new AccountDeletionRequest
            {
                UserId = userId,
                Status = hasActiveDelivery
                    ? AccountDeletionStatus.PendingActiveDelivery
                    : AccountDeletionStatus.Scheduled,
                RequestedAt = now,
                ScheduledPurgeAt = hasActiveDelivery ? null : now + TimeSpan.FromDays(30),
                AnonymizedUserHash = InMemoryAccountDeletionStore.HashUserId(userId),
            });
            return Task.FromResult(record);
        }

        public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct) =>
            Task.FromResult(_records.TryGetValue(userId, out var record) ? record : null);

        public Task<IReadOnlyList<AccountDeletionRequest>> AdvanceAsync(DateTimeOffset now, CancellationToken ct)
        {
            Advances++;
            return Task.FromResult<IReadOnlyList<AccountDeletionRequest>>(Array.Empty<AccountDeletionRequest>());
        }
    }

    private sealed class RecordingOwnershipClient : StubOwnershipClient
    {
        public List<(string IdempotencyKey, WorkItemCreateRequestV1 Body)> Creates { get; } = new();

        public int Reads { get; private set; }

        public override Task<WorkItemRecordV1> CreateWorkItemAsync(
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

        public override Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<WorkItemRecordV1?>(null);
        }
    }

    private sealed class ThrowingOwnershipClient : StubOwnershipClient
    {
        public int Attempts { get; private set; }

        public override Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Attempts++;
            throw new HttpRequestException("state-service is down");
        }
    }

    private abstract class StubOwnershipClient : IStateOwnershipClient
    {
        public virtual Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();

        public virtual Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct) => throw new NotSupportedException();

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) => throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct) => throw new NotSupportedException();

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct) => throw new NotSupportedException();
    }
}
