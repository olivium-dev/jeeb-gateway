using System.Text.Json;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Notifications;
using JeebGateway.Push;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Services.Dispatch;
using JeebGateway.StateService.Ownership;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.StateService;

/// <summary>W1-10/W1-12 — the claim worker that closes the W1-09 gap: complete/fail ride the
/// lease the batch claim actually handed out, and the inline producers only enqueue.</summary>
public class WorkItemClaimWorkerTests
{
    private static readonly Guid Recipient = Guid.Parse("6b2b0d4f-98a1-4e5a-9a1a-2f1c0a7a5b31");

    [Fact]
    public async Task The_Worker_Completes_A_Notification_Item_It_Holds_A_Lease_For()
    {
        var state = new FakeStateOwnershipClient();
        var push = new CapturingGenericEventDispatcher();
        var dispatcher = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(),
            push,
            new StateServiceNotificationDispatchOutbox(
                state, NullLogger<StateServiceNotificationDispatchOutbox>.Instance),
            NullLogger<JeebNotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            "jeeb.delivery.completed", "en",
            new Dictionary<string, string> { ["requestId"] = "req-77" },
            Recipient, "notif:delivery-completed:req-77", CancellationToken.None);

        push.Sent.Should().BeEmpty("the inline dispatcher enqueues only once the outbox is claim-driven");

        // The claimed record carries the payload the enqueue actually wrote — a real hop.
        var created = state.Created.Should().ContainSingle().Subject.Body;
        var workItemId = Guid.NewGuid();
        var lease = Guid.NewGuid();
        state.Claimable.Add(new WorkItemRecordV1
        {
            WorkItemId = workItemId,
            Application = "jeeb-gateway",
            Kind = created.Kind,
            SubjectRef = created.SubjectRef,
            Status = "leased",
            Payload = created.Payload!.Value,
            MaxAttempts = 3,
            Version = 9,
            LeaseToken = lease,
        });

        var claimed = await Worker(state, push, notificationOutboxMode: "upstream-authority")
            .RunOnceAsync(CancellationToken.None);

        claimed.Should().Be(1);
        var claim = state.Claims.Should().ContainSingle().Subject;
        claim.Application.Should().Be("jeeb-gateway");
        claim.Kinds.Should().ContainSingle().Which.Should().Be("notification-dispatch");
        claim.WorkerId.Should().NotBeNullOrWhiteSpace();
        claim.LeaseSeconds.Should().Be(120);

        push.Sent.Should().ContainSingle().Which.EntityId
            .Should().Be("notif:delivery-completed:req-77",
                "the entry's own key must reach PushDispatch through the claimer (§4.1 rider)");

        var completed = state.Completed.Should().ContainSingle().Subject;
        completed.WorkItemId.Should().Be(workItemId);
        completed.Body.LeaseToken.Should().Be(lease, "the complete must ride the lease we hold");
        completed.Body.ExpectedVersion.Should().Be(9);
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Failed_Dispatch_Fails_The_Item_Under_The_Same_Lease_With_A_Future_RetryAt()
    {
        var state = new FakeStateOwnershipClient();
        var push = new CapturingGenericEventDispatcher();
        var workItemId = Guid.NewGuid();
        var lease = Guid.NewGuid();
        state.Claimable.Add(NotificationRecord(
            workItemId, lease, version: 3, templateKey: "jeeb.template.does.not.exist"));

        await Worker(state, push, notificationOutboxMode: "upstream-authority")
            .RunOnceAsync(CancellationToken.None);

        push.Sent.Should().BeEmpty();
        state.Completed.Should().BeEmpty();
        var failed = state.Failed.Should().ContainSingle().Subject;
        failed.WorkItemId.Should().Be(workItemId);
        failed.Body.LeaseToken.Should().Be(lease);
        failed.Body.ExpectedVersion.Should().Be(3);
        failed.Body.Error.Should().Contain("jeeb.template.does.not.exist");
        failed.Body.RetryAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task An_Item_Claimed_Without_A_Lease_Token_Is_Never_Cas_Guessed()
    {
        var state = new FakeStateOwnershipClient();
        var push = new CapturingGenericEventDispatcher();
        var record = NotificationRecord(Guid.NewGuid(), lease: null, version: 1);
        state.Claimable.Add(record);

        await Worker(state, push, notificationOutboxMode: "upstream-authority")
            .RunOnceAsync(CancellationToken.None);

        push.Sent.Should().BeEmpty();
        state.Completed.Should().BeEmpty();
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Worker_Claims_Nothing_While_Both_Gates_Sit_At_Their_Shipped_Defaults()
    {
        new GwdbxMigrationOptions().NotificationOutboxMode.Should().Be("local");
        new GwdbxMigrationOptions().NotificationOutbox.Should().Be(GwdbxMigrationPhase.Local);
        new UpstreamFeatureFlags().FanoutWorkQueue.Should().BeFalse();

        var state = new FakeStateOwnershipClient();
        state.Claimable.Add(NotificationRecord(Guid.NewGuid(), Guid.NewGuid(), version: 1));

        var claimed = await Worker(state, new CapturingGenericEventDispatcher())
            .RunOnceAsync(CancellationToken.None);

        claimed.Should().Be(0);
        state.Claims.Should().BeEmpty("a gated-off kind is never even claimed");
        state.Completed.Should().BeEmpty();
        state.Failed.Should().BeEmpty();
    }

    [Fact]
    public void The_Claimer_Scopes_Itself_To_The_Same_Application_As_Both_Producers()
    {
        // A drifted application string would silently claim nothing — the exact bug this closes.
        WorkItemClaimWorker.Application.Should().Be(StateServiceNotificationDispatchOutbox.Application);
        WorkItemClaimWorker.Application.Should().Be(StateServiceNewRequestFanoutWorkItems.Application);
    }

    [Fact]
    public async Task A_Gated_Off_Kind_Does_Not_Drag_Its_Client_Graph_Into_The_Boot_Path()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStateOwnershipClient>(new FakeStateOwnershipClient());
        services.AddSingleton<IOptions<UpstreamFeatureFlags>>(Options.Create(new UpstreamFeatureFlags()));
        // INewRequestPushNotifier is deliberately NOT registered: reading Enabled must not need it.
        services.AddScoped<IWorkItemExecutor, NewRequestFanoutWorkItemExecutor>();
        var provider = services.BuildServiceProvider();

        var worker = new WorkItemClaimWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<WorkItemClaimWorker>.Instance);

        (await worker.RunOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task The_Worker_Fans_Out_And_Completes_A_New_Request_Fanout_Item_Under_Its_Lease()
    {
        var state = new FakeStateOwnershipClient();
        var notifier = new RecordingFanoutNotifier();
        var workItemId = Guid.NewGuid();
        var lease = Guid.NewGuid();
        state.Claimable.Add(new WorkItemRecordV1
        {
            WorkItemId = workItemId,
            Application = "jeeb-gateway",
            Kind = "new-request-fanout",
            SubjectRef = "req-31",
            Status = "leased",
            // Byte-identical to what StateServiceNewRequestFanoutWorkItems.PersistAsync writes.
            Payload = JsonSerializer.SerializeToElement(
                new NewRequestNotification("req-31", "tier-1", "two bags", "customer-1", 33.8, 35.5)),
            Version = 5,
            LeaseToken = lease,
        });

        var claimed = await Worker(
                state, new CapturingGenericEventDispatcher(), fanoutWorkQueue: true, notifier: notifier)
            .RunOnceAsync(CancellationToken.None);

        claimed.Should().Be(1);
        state.Claims.Should().ContainSingle().Which.Kinds.Should().ContainSingle()
            .Which.Should().Be("new-request-fanout");
        notifier.Jobs.Should().ContainSingle().Which.RequestId.Should().Be("req-31");
        var completed = state.Completed.Should().ContainSingle().Subject;
        completed.WorkItemId.Should().Be(workItemId);
        completed.Body.LeaseToken.Should().Be(lease);
        completed.Body.ExpectedVersion.Should().Be(5);
    }

    [Fact]
    public async Task The_Inline_Dispatcher_Calls_Neither_Complete_Nor_Fail_When_The_Outbox_Is_Claim_Driven()
    {
        var outbox = new RecordingOutbox { IsClaimDriven = true };
        var push = new CapturingGenericEventDispatcher();
        var dispatcher = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(), push, outbox,
            NullLogger<JeebNotificationDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            "jeeb.delivery.completed", "en",
            new Dictionary<string, string> { ["requestId"] = "req-77" },
            Recipient, "notif:delivery-completed:req-78", CancellationToken.None);

        outbox.Added.Should().Be(1);
        outbox.Delivered.Should().Be(0, "the inline path holds no lease, so it must not pretend to complete");
        outbox.Failures.Should().Be(0);
        push.Sent.Should().BeEmpty();
        result.Status.Should().Be(NotificationDispatchStatus.Pending);
    }

    [Fact]
    public async Task An_Unknown_Template_Is_Still_A_Caller_Visible_Failure_In_Claim_Driven_Mode()
    {
        var outbox = new RecordingOutbox { IsClaimDriven = true };
        var push = new CapturingGenericEventDispatcher();
        var dispatcher = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(), push, outbox,
            NullLogger<JeebNotificationDispatcher>.Instance);

        var result = await dispatcher.DispatchAsync(
            "jeeb.template.does.not.exist", "en", new Dictionary<string, string>(),
            Recipient, "notif:unknown-template", CancellationToken.None);

        result.Status.Should().Be(NotificationDispatchStatus.DLQ, "the controller still answers 400");
        outbox.Delivered.Should().Be(0);
        outbox.Failures.Should().Be(0);
        push.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task The_Local_Outbox_Path_Still_Pushes_Inline_And_Marks_Delivered()
    {
        var outbox = new RecordingOutbox { IsClaimDriven = false };
        var push = new CapturingGenericEventDispatcher();
        var dispatcher = new JeebNotificationDispatcher(
            new StaticNotificationTemplateRenderer(), push, outbox,
            NullLogger<JeebNotificationDispatcher>.Instance);

        await dispatcher.DispatchAsync(
            "jeeb.delivery.completed", "en",
            new Dictionary<string, string> { ["requestId"] = "req-77" },
            Recipient, "notif:delivery-completed:req-79", CancellationToken.None);

        outbox.Added.Should().Be(1);
        push.Sent.Should().ContainSingle();
        outbox.Delivered.Should().Be(1, "the local rail is unchanged by W1-10");
    }

    [Fact]
    public async Task The_Expiry_Notifier_Calls_Neither_Complete_Nor_Fail_When_The_Outbox_Is_Claim_Driven()
    {
        var outbox = new RecordingOutbox { IsClaimDriven = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INotificationDispatchOutbox>(outbox);
        services.AddSingleton<INotificationTemplateRenderer, StaticNotificationTemplateRenderer>();
        var provider = services.BuildServiceProvider();

        var notifier = new DispatchingRequestExpiryNotifier(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DispatchingRequestExpiryNotifier>.Instance);

        await notifier.NotifyExpiredAsync(
            Recipient.ToString("D"), "req-88", DateTimeOffset.UtcNow, CancellationToken.None);

        outbox.Added.Should().Be(1);
        outbox.Delivered.Should().Be(0);
        outbox.Failures.Should().Be(0);
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 1)]
    public async Task The_Fanout_Drain_Leaves_Persisted_Jobs_To_The_Claim_Worker(
        bool flagOn, bool persisted, int inlineFanOuts)
    {
        var notifier = new RecordingFanoutNotifier();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<INewRequestFanoutWorkItems>(new StubFanoutWorkItems(persisted));
        services.AddSingleton<INewRequestPushNotifier>(notifier);
        services.AddSingleton<IOptions<UpstreamFeatureFlags>>(
            Options.Create(new UpstreamFeatureFlags { FanoutWorkQueue = flagOn }));
        var provider = services.BuildServiceProvider();

        var queue = new NewRequestFanoutQueue(4);
        queue.TryEnqueue(new NewRequestNotification("req-42", null, null, "customer-1", null, null))
            .Should().BeTrue();
        var processor = new NewRequestFanoutProcessor(
            provider, queue, NullLogger<NewRequestFanoutProcessor>.Instance);

        (await processor.DrainOnceAsync(CancellationToken.None)).Should().Be(1);

        notifier.Jobs.Should().HaveCount(inlineFanOuts);
    }

    private static WorkItemClaimWorker Worker(
        FakeStateOwnershipClient state,
        IGenericEventDispatcher push,
        string notificationOutboxMode = "local",
        bool fanoutWorkQueue = false,
        INewRequestPushNotifier? notifier = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStateOwnershipClient>(state);
        services.AddSingleton<INotificationTemplateRenderer, StaticNotificationTemplateRenderer>();
        services.AddSingleton<IGenericEventDispatcher>(push);
        services.AddSingleton<INewRequestPushNotifier>(notifier ?? new RecordingFanoutNotifier());
        services.AddSingleton<IOptions<GwdbxMigrationOptions>>(
            Options.Create(new GwdbxMigrationOptions { NotificationOutboxMode = notificationOutboxMode }));
        services.AddSingleton<IOptions<UpstreamFeatureFlags>>(
            Options.Create(new UpstreamFeatureFlags { FanoutWorkQueue = fanoutWorkQueue }));
        services.AddScoped<IWorkItemExecutor, NotificationDispatchWorkItemExecutor>();
        services.AddScoped<IWorkItemExecutor, NewRequestFanoutWorkItemExecutor>();
        var provider = services.BuildServiceProvider();

        return new WorkItemClaimWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            TimeProvider.System,
            NullLogger<WorkItemClaimWorker>.Instance);
    }

    private static WorkItemRecordV1 NotificationRecord(
        Guid workItemId, Guid? lease, int version, string templateKey = "jeeb.delivery.completed")
    {
        var entry = new NotificationDispatchEntry
        {
            TemplateKey = templateKey,
            Locale = "en",
            Parameters = new Dictionary<string, string> { ["requestId"] = "req-77" },
            RecipientUserId = Recipient,
            IdempotencyKey = "notif:" + templateKey,
        };
        return new WorkItemRecordV1
        {
            WorkItemId = workItemId,
            Application = "jeeb-gateway",
            Kind = "notification-dispatch",
            SubjectRef = StateServiceNotificationDispatchOutbox.SubjectRefFor(entry),
            Status = "leased",
            Payload = JsonSerializer.SerializeToElement(
                StateServiceNotificationDispatchOutbox.OutboxPayloadV1.From(entry),
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            MaxAttempts = 3,
            Version = version,
            LeaseToken = lease,
        };
    }

    private sealed class StubFanoutWorkItems : INewRequestFanoutWorkItems
    {
        private readonly bool _persisted;

        public StubFanoutWorkItems(bool persisted) => _persisted = persisted;

        public Task<bool> PersistAsync(NewRequestNotification job, CancellationToken ct) =>
            Task.FromResult(_persisted);
    }

    private sealed class RecordingFanoutNotifier : INewRequestPushNotifier
    {
        public List<NewRequestNotification> Jobs { get; } = new();

        public Task NotifyNewRequestAsync(NewRequestNotification notification, CancellationToken ct) =>
            Task.CompletedTask;

        public Task FanOutAsync(NewRequestNotification notification, CancellationToken ct)
        {
            Jobs.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingOutbox : INotificationDispatchOutbox
    {
        public bool IsClaimDriven { get; init; }
        public int Added { get; private set; }
        public int Delivered { get; private set; }
        public int Failures { get; private set; }
        public int PendingCount => 0;

        public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<NotificationDispatchEntry> AddAsync(
            NotificationDispatchEntry entry, CancellationToken ct = default)
        {
            Added++;
            return Task.FromResult(entry);
        }

        public Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(
            DateTimeOffset now, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(Array.Empty<NotificationDispatchEntry>());

        public Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default)
        {
            Delivered++;
            return Task.CompletedTask;
        }

        public Task RecordFailureAsync(
            Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default)
        {
            Failures++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(Array.Empty<NotificationDispatchEntry>());
    }

    private sealed class FakeStateOwnershipClient : IStateOwnershipClient
    {
        public List<(string IdempotencyKey, WorkItemCreateRequestV1 Body)> Created { get; } = new();
        public List<WorkClaimRequestV1> Claims { get; } = new();
        public List<(Guid WorkItemId, WorkCompleteRequestV1 Body)> Completed { get; } = new();
        public List<(Guid WorkItemId, WorkFailRequestV1 Body)> Failed { get; } = new();
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
            string application, string kind, string subjectRef, CancellationToken ct) =>
            Task.FromResult<WorkItemRecordV1?>(null);

        public Task<IReadOnlyList<WorkItemRecordV1>> ClaimWorkItemsAsync(
            WorkClaimRequestV1 body, CancellationToken ct)
        {
            Claims.Add(body);
            IReadOnlyList<WorkItemRecordV1> claimed = Claimable
                .Where(item => body.Kinds is null || body.Kinds.Contains(item.Kind))
                .ToArray();
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

        public Task<WorkItemRecordV1> ConsumeWorkItemAsync(
            Guid workItemId, WorkConsumeRequestV1 body, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventRecordV1> AppendAuditEventAsync(
            AuditEventAppendRequestV1 body, string idempotencyKey, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<AuditEventPageV1> FindAuditEventsAsync(AuditEventQueryV1 query, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
