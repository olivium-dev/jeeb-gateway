using System.Text.Json;
using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Migration;
using JeebGateway.Push;
using JeebGateway.Services.Clients;
using JeebGateway.Services.Dispatch;
using JeebGateway.StateService.Ownership;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.StateService;

/// <summary>W1-09 — the notification outbox on state-service /v1/work-items
/// (kind=notification-dispatch): enqueue, claim, complete/fail, Idempotency-Key passthrough.</summary>
public class StateServiceNotificationDispatchOutboxTests
{
    private static readonly Guid Recipient = Guid.Parse("6b2b0d4f-98a1-4e5a-9a1a-2f1c0a7a5b31");

    private static NotificationDispatchEntry NewEntry(string? idempotencyKey) => new()
    {
        TemplateKey = "jeeb.delivery.completed",
        Locale = "en",
        Parameters = new Dictionary<string, string> { ["requestId"] = "req-77" },
        RecipientUserId = Recipient,
        IdempotencyKey = idempotencyKey,
    };

    private static StateServiceNotificationDispatchOutbox Outbox(FakeStateOwnershipClient state) =>
        new(state, NullLogger<StateServiceNotificationDispatchOutbox>.Instance);

    [Fact]
    public async Task Enqueue_Creates_A_NotificationDispatch_Work_Item_With_The_Entry_Payload()
    {
        var state = new FakeStateOwnershipClient();
        var entry = NewEntry("notif:delivery-completed:req-77");
        entry.NextAttemptAt = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

        await Outbox(state).AddAsync(entry, CancellationToken.None);

        var created = state.Created.Should().ContainSingle().Subject;
        created.Body.Application.Should().Be("jeeb-gateway");
        created.Body.Kind.Should().Be("notification-dispatch");
        created.Body.SubjectRef.Should().Be("notif:delivery-completed:req-77");
        created.Body.MaxAttempts.Should().Be(3);
        created.Body.DueAt.Should().Be(entry.NextAttemptAt);

        var payload = created.Body.Payload!.Value;
        payload.GetProperty("id").GetGuid().Should().Be(entry.Id);
        payload.GetProperty("templateKey").GetString().Should().Be("jeeb.delivery.completed");
        payload.GetProperty("locale").GetString().Should().Be("en");
        payload.GetProperty("recipientUserId").GetGuid().Should().Be(Recipient);
        payload.GetProperty("parameters").GetProperty("requestId").GetString().Should().Be("req-77");
    }

    [Fact]
    public async Task Enqueue_Passes_The_Notification_Idempotency_Key_Through_To_State_Service()
    {
        var state = new FakeStateOwnershipClient();

        await Outbox(state).AddAsync(NewEntry("notif:offer-accepted:req-9"), CancellationToken.None);

        var created = state.Created.Should().ContainSingle().Subject;
        created.IdempotencyKey.Should().Be("notification-dispatch:notif:offer-accepted:req-9");
        created.Body.SubjectRef.Should().Be("notif:offer-accepted:req-9");
        created.Body.Payload!.Value.GetProperty("idempotencyKey").GetString()
            .Should().Be("notif:offer-accepted:req-9",
                "the key must survive the outbox hop so PushDispatch can dedupe on it (§4.1 rider)");
    }

    [Fact]
    public async Task Enqueue_Without_An_Idempotency_Key_Falls_Back_To_The_Entry_Id()
    {
        var state = new FakeStateOwnershipClient();
        var entry = NewEntry(idempotencyKey: null);

        await Outbox(state).AddAsync(entry, CancellationToken.None);

        var created = state.Created.Should().ContainSingle().Subject;
        created.Body.SubjectRef.Should().Be(entry.Id.ToString("D"));
        created.IdempotencyKey.Should().Be("notification-dispatch:" + entry.Id.ToString("D"));
    }

    [Fact]
    public async Task Exists_Probes_The_Scoped_Latest_Work_Item()
    {
        var state = new FakeStateOwnershipClient();
        var outbox = Outbox(state);

        (await outbox.ExistsAsync("notif:missing", CancellationToken.None)).Should().BeFalse();

        state.Latest["notif:present"] = Record(Guid.NewGuid(), NewEntry("notif:present"), Guid.NewGuid(), 1);
        (await outbox.ExistsAsync("notif:present", CancellationToken.None)).Should().BeTrue();
        state.LatestQueries.Should().OnlyContain(q => q.Application == "jeeb-gateway"
            && q.Kind == "notification-dispatch");
    }

    [Fact]
    public async Task Claim_Returns_Leased_Entries_And_Preserves_The_Idempotency_Key()
    {
        var source = NewEntry("notif:chat:42");
        var state = new FakeStateOwnershipClient();
        state.Claimable.Add(Record(Guid.NewGuid(), source, Guid.NewGuid(), version: 4));

        var due = await Outbox(state).GetDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var claim = state.Claims.Should().ContainSingle().Subject;
        claim.Application.Should().Be("jeeb-gateway");
        claim.Kinds.Should().ContainSingle().Which.Should().Be("notification-dispatch");
        claim.WorkerId.Should().NotBeNullOrWhiteSpace();
        claim.LeaseSeconds.Should().Be(120);

        var entry = due.Should().ContainSingle().Subject;
        entry.Id.Should().Be(source.Id);
        entry.TemplateKey.Should().Be("jeeb.delivery.completed");
        entry.RecipientUserId.Should().Be(Recipient);
        entry.IdempotencyKey.Should().Be("notif:chat:42");
        entry.Parameters["requestId"].Should().Be("req-77");
    }

    [Fact]
    public async Task Delivered_Completes_The_Claimed_Item_Under_Lease_And_Version_Cas()
    {
        var source = NewEntry("notif:chat:43");
        var workItemId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var state = new FakeStateOwnershipClient();
        state.Claimable.Add(Record(workItemId, source, leaseToken, version: 7));
        var outbox = Outbox(state);

        await outbox.GetDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await outbox.MarkDeliveredAsync(source.Id, CancellationToken.None);

        var completed = state.Completed.Should().ContainSingle().Subject;
        completed.WorkItemId.Should().Be(workItemId);
        completed.Body.LeaseToken.Should().Be(leaseToken);
        completed.Body.ExpectedVersion.Should().Be(7);
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task Failure_Fails_The_Claimed_Item_With_A_Future_RetryAt()
    {
        var source = NewEntry("notif:chat:44");
        var workItemId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var state = new FakeStateOwnershipClient();
        state.Claimable.Add(Record(workItemId, source, leaseToken, version: 2));
        var outbox = Outbox(state);

        await outbox.GetDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await outbox.RecordFailureAsync(
            source.Id, "transport blew up", maxAttempts: 3, TimeSpan.FromSeconds(30), CancellationToken.None);

        var failed = state.Failed.Should().ContainSingle().Subject;
        failed.WorkItemId.Should().Be(workItemId);
        failed.Body.LeaseToken.Should().Be(leaseToken);
        failed.Body.ExpectedVersion.Should().Be(2);
        failed.Body.Error.Should().Be("transport blew up");
        failed.Body.RetryAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task A_Zero_Retry_Delay_Sends_A_Null_RetryAt_Instead_Of_A_Past_One()
    {
        var source = NewEntry("notif:chat:45");
        var state = new FakeStateOwnershipClient();
        state.Claimable.Add(Record(Guid.NewGuid(), source, Guid.NewGuid(), version: 1));
        var outbox = Outbox(state);

        await outbox.GetDueAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        await outbox.RecordFailureAsync(
            source.Id, "expired", maxAttempts: 1, TimeSpan.Zero, CancellationToken.None);

        // A non-future retryAt is rejected 400 (work.retry_at.invalid); null fails the item now.
        state.Failed.Should().ContainSingle().Which.Body.RetryAt.Should().BeNull();
    }

    [Fact]
    public async Task An_Unclaimed_Entry_Is_Left_Due_Instead_Of_Being_Cas_Guessed()
    {
        var state = new FakeStateOwnershipClient();
        var outbox = Outbox(state);

        await outbox.MarkDeliveredAsync(Guid.NewGuid(), CancellationToken.None);
        await outbox.RecordFailureAsync(
            Guid.NewGuid(), "unknown template", 3, TimeSpan.FromSeconds(30), CancellationToken.None);

        state.Completed.Should().BeEmpty();
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Dispatcher_Hands_The_Same_Idempotency_Key_To_The_Push_Request()
    {
        var state = new FakeStateOwnershipClient();
        var push = new CapturingPushNotificationService();
        var dispatcher = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(),
            push,
            Outbox(state),
            NullLogger<JeebNotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            "jeeb.delivery.completed", "en",
            new Dictionary<string, string> { ["requestId"] = "req-77" },
            Recipient, "notif:delivery-completed:req-77", CancellationToken.None);

        state.Created.Should().ContainSingle().Which.IdempotencyKey
            .Should().Be("notification-dispatch:notif:delivery-completed:req-77");
        push.Sent.Should().ContainSingle().Which.IdempotencyKey
            .Should().Be("notif:delivery-completed:req-77");

        // W1-10 BLOCKER, pinned: the dispatcher never claims, so the item stays queued+claimable —
        // flipping before the claim side owns dispatch backs up the queue and re-pushes delivered work.
        state.Completed.Should().BeEmpty(
            "the inline dispatcher cannot complete its own work item without a claim lease");
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public void The_Adapter_Is_An_Approved_Durable_Resolution_For_The_Outbox()
    {
        var durable = StoreDurabilityGuard.Critical
            .Single(entry => entry.Iface == typeof(INotificationDispatchOutbox)).DurableImpls;

        durable.Should().Contain(typeof(StateServiceNotificationDispatchOutbox),
            "a NotificationOutboxMode=upstream-authority boot must not be fail-closed (G-08)");
        durable.Should().Contain(typeof(PostgresNotificationDispatchOutbox));
        durable.Should().NotContain(typeof(InMemoryNotificationDispatchOutbox));
    }

    [Fact]
    public void The_Outbox_Mode_Key_Defaults_To_Local_On_The_Shared_A10_Ladder()
    {
        var options = new GwdbxMigrationOptions();

        options.NotificationOutboxMode.Should().Be("local");
        options.NotificationOutbox.Should().Be(GwdbxMigrationPhase.Local);
        GwdbxMigrationOptions.IsKnown("upstream-authority").Should().BeTrue();
        GwdbxMigrationOptions.IsKnown("drain-and-switch").Should().BeFalse();
    }

    private static WorkItemRecordV1 Record(
        Guid workItemId, NotificationDispatchEntry entry, Guid leaseToken, int version) => new()
    {
        WorkItemId = workItemId,
        Application = "jeeb-gateway",
        Kind = "notification-dispatch",
        SubjectRef = StateServiceNotificationDispatchOutbox.SubjectRefFor(entry),
        Status = "leased",
        Payload = JsonSerializer.SerializeToElement(
            StateServiceNotificationDispatchOutbox.OutboxPayloadV1.From(entry),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        DueAt = DateTimeOffset.UtcNow,
        MaxAttempts = 3,
        Version = version,
        LeaseToken = leaseToken,
    };

    private sealed class CapturingPushNotificationService : IPushNotificationService
    {
        public List<PushNotificationRequest> Sent { get; } = new();

        public Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(new PushDeliveryResult(
                request.UserId, request.Trigger, PushDeliveryOutcome.Delivered, 0));
        }
    }

    private sealed class FakeStateOwnershipClient : IStateOwnershipClient
    {
        public List<(string IdempotencyKey, WorkItemCreateRequestV1 Body)> Created { get; } = new();
        public List<WorkClaimRequestV1> Claims { get; } = new();
        public List<(Guid WorkItemId, WorkCompleteRequestV1 Body)> Completed { get; } = new();
        public List<(Guid WorkItemId, WorkFailRequestV1 Body)> Failed { get; } = new();
        public List<(string Application, string Kind, string SubjectRef)> LatestQueries { get; } = new();
        public Dictionary<string, WorkItemRecordV1> Latest { get; } = new(StringComparer.Ordinal);
        public List<WorkItemRecordV1> Claimable { get; } = new();

        public Task<WorkItemRecordV1> CreateWorkItemAsync(
            WorkItemCreateRequestV1 body, string idempotencyKey, CancellationToken ct)
        {
            Created.Add((idempotencyKey, body));
            return Task.FromResult(new WorkItemRecordV1
            {
                WorkItemId = Guid.NewGuid(),
                Application = body.Application,
                Kind = body.Kind,
                SubjectRef = body.SubjectRef,
                Status = "queued",
                Payload = body.Payload ?? default,
                MaxAttempts = body.MaxAttempts ?? 0,
            });
        }

        public Task<WorkItemRecordV1?> GetLatestWorkItemAsync(
            string application, string kind, string subjectRef, CancellationToken ct)
        {
            LatestQueries.Add((application, kind, subjectRef));
            return Task.FromResult(Latest.TryGetValue(subjectRef, out var record) ? record : null);
        }

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct)
        {
            Claims.Add(body);
            IReadOnlyList<WorkItemRecordV1> claimed = Claimable.ToArray();
            Claimable.Clear();
            return Task.FromResult(claimed);
        }

        public Task<WorkItemRecordV1> CompleteWorkItemAsync(
            Guid workItemId, WorkCompleteRequestV1 body, CancellationToken ct)
        {
            Completed.Add((workItemId, body));
            return Task.FromResult(new WorkItemRecordV1 { WorkItemId = workItemId, Status = "completed" });
        }

        public Task<WorkItemRecordV1> FailWorkItemAsync(
            Guid workItemId, WorkFailRequestV1 body, CancellationToken ct)
        {
            Failed.Add((workItemId, body));
            return Task.FromResult(new WorkItemRecordV1 { WorkItemId = workItemId, Status = "queued" });
        }

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
