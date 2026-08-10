using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DurableWorkSweepExecutorTests
{
    [Fact]
    public async Task Sweep_Claims_A_Bounded_Batch_And_Respects_Max_Concurrency()
    {
        var work = new FakeStateWorkClient(
            Enumerable.Range(0, 5).Select(index => Item(index)).ToArray());
        var active = 0;
        var maximum = 0;
        var handler = new DelegateHandler("test-kind", async (_, ct) =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maximum, current);
            try
            {
                await Task.Delay(25, ct);
                return DurableWorkExecutionResult.Completed();
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        });
        var executor = Executor(
            work,
            handler,
            new DurableWorkExecutionOptions
            {
                MaxBatchSize = 5,
                MaxConcurrency = 2,
                LeaseSeconds = 30,
                LeaseRenewInterval = TimeSpan.FromSeconds(10),
            });

        var summary = await executor.SweepAsync("test-kind", 99, CancellationToken.None);

        summary.Claimed.Should().Be(5);
        summary.Completed.Should().Be(5);
        maximum.Should().Be(2);
        work.Claims.Should().ContainSingle();
        work.Claims[0].Application.Should().Be(DurableWorkContract.Application);
        work.Claims[0].Kinds.Should().Equal("test-kind");
        work.Claims[0].Limit.Should().Be(5, "caller limits are capped by MaxBatchSize");
        work.Completes.Should().HaveCount(5);
        work.Completes.Should().OnlyContain(call =>
            call.Request.LeaseToken == call.Item.LeaseToken
            && call.Request.ExpectedVersion == call.Item.Version);
    }

    [Fact]
    public async Task Long_Running_Handler_Renews_Lease_And_Terminal_Cas_Uses_New_Version()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
        var claimed = Item(1, version: 7);
        var work = new FakeStateWorkClient([claimed]);
        var renewed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.OnRenew = (_, request, _) =>
        {
            request.ExpectedVersion.Should().Be(7);
            renewed.TrySetResult();
            return Task.FromResult(Clone(claimed, version: 8));
        };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler("test-kind", async (_, ct) =>
        {
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
            return DurableWorkExecutionResult.Completed();
        });
        var executor = Executor(work, handler, Options(), clock);

        var sweep = executor.SweepAsync("test-kind", null, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await renewed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        finish.TrySetResult();

        var summary = await sweep.WaitAsync(TimeSpan.FromSeconds(2));
        summary.Completed.Should().Be(1);
        work.Renewals.Should().ContainSingle();
        work.Completes.Should().ContainSingle();
        var complete = work.Completes.Single();
        complete.Request.ExpectedVersion.Should().Be(8);
        complete.Request.LeaseToken.Should().Be(claimed.LeaseToken!.Value);
    }

    [Fact]
    public async Task Renewal_Conflict_Cancels_Handler_And_Suppresses_All_Terminal_Mutations()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
        var work = new FakeStateWorkClient([Item(2)]);
        var renewalAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        work.OnRenew = (_, _, _) =>
        {
            renewalAttempted.TrySetResult();
            return Task.FromException<StateWorkItem>(new StateWorkConflictException("renew"));
        };
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler("test-kind", async (_, ct) =>
        {
            handlerStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return DurableWorkExecutionResult.Completed();
        });
        var executor = Executor(work, handler, Options(), clock);

        var sweep = executor.SweepAsync("test-kind", null, CancellationToken.None);
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(2));
        await renewalAttempted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var summary = await sweep.WaitAsync(TimeSpan.FromSeconds(2));
        summary.LeaseLost.Should().Be(1);
        summary.Completed.Should().Be(0);
        summary.Failed.Should().Be(0);
        work.Completes.Should().BeEmpty();
        work.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task Retry_Uses_The_Claim_Lease_And_Exact_Version()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
        var claimed = Item(3, version: 11);
        var retryAt = clock.GetUtcNow() + TimeSpan.FromHours(6);
        var work = new FakeStateWorkClient([claimed]);
        var handler = new DelegateHandler(
            "test-kind",
            (_, _) => Task.FromResult(
                DurableWorkExecutionResult.Retry("owner-not-ready", retryAt)));
        var executor = Executor(work, handler, Options(), clock);

        var summary = await executor.SweepAsync("test-kind", null, CancellationToken.None);

        summary.Retried.Should().Be(1);
        work.Failures.Should().ContainSingle();
        var failure = work.Failures.Single();
        failure.Request.LeaseToken.Should().Be(claimed.LeaseToken!.Value);
        failure.Request.ExpectedVersion.Should().Be(11);
        failure.Request.Error.Should().Be("owner-not-ready");
        failure.Request.RetryAt.Should().Be(retryAt);
        work.Completes.Should().BeEmpty();
    }

    [Fact]
    public async Task Expected_Wait_Uses_Defer_Even_On_The_Last_Failure_Attempt()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-08-10T12:00:00Z"));
        var claimed = Item(4, version: 13, attempts: 10, maxAttempts: 10);
        var dueAt = clock.GetUtcNow() + TimeSpan.FromDays(30);
        var work = new FakeStateWorkClient([claimed]);
        var handler = new DelegateHandler(
            "test-kind",
            (_, _) => Task.FromResult(
                DurableWorkExecutionResult.Deferred("expected-owner-wait", dueAt)));
        var executor = Executor(work, handler, Options(), clock);

        var summary = await executor.SweepAsync("test-kind", null, CancellationToken.None);

        summary.Deferred.Should().Be(1);
        summary.Retried.Should().Be(0);
        work.Deferrals.Should().ContainSingle();
        var deferred = work.Deferrals.Single();
        deferred.Request.LeaseToken.Should().Be(claimed.LeaseToken!.Value);
        deferred.Request.ExpectedVersion.Should().Be(13);
        deferred.Request.DueAt.Should().Be(dueAt);
        deferred.Request.Reason.Should().Be("expected-owner-wait");
        work.Failures.Should().BeEmpty(
            "expected waits must not enter the failure-attempt mutation");
    }

    private static DurableWorkExecutionOptions Options() => new()
    {
        MaxBatchSize = 10,
        MaxConcurrency = 1,
        LeaseSeconds = 10,
        LeaseRenewInterval = TimeSpan.FromSeconds(2),
        WorkerId = "test-worker",
    };

    private static DurableWorkSweepExecutor Executor(
        FakeStateWorkClient work,
        IDurableWorkItemHandler handler,
        DurableWorkExecutionOptions options,
        TimeProvider? clock = null) => new(
        work,
        [handler],
        Microsoft.Extensions.Options.Options.Create(options),
        clock ?? TimeProvider.System,
        NullLogger<DurableWorkSweepExecutor>.Instance);

    private static StateWorkItem Item(
        int index,
        int version = 3,
        int attempts = 1,
        int maxAttempts = 10) => new()
    {
        WorkItemId = Guid.NewGuid(),
        Application = DurableWorkContract.Application,
        Kind = "test-kind",
        SubjectRef = $"subject-{index}",
        Status = "leased",
        Payload = JsonSerializer.SerializeToElement(new { index }),
        DueAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
        Attempts = attempts,
        MaxAttempts = maxAttempts,
        Version = version,
        LeaseToken = Guid.NewGuid(),
        LeasedBy = "test-worker",
        LeasedUntil = DateTimeOffset.Parse("2026-08-10T12:00:10Z"),
        CreatedAt = DateTimeOffset.Parse("2026-08-10T11:00:00Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z"),
    };

    private static StateWorkItem Clone(StateWorkItem source, int version) => new()
    {
        WorkItemId = source.WorkItemId,
        Application = source.Application,
        Kind = source.Kind,
        SubjectRef = source.SubjectRef,
        Status = source.Status,
        Payload = source.Payload,
        Result = source.Result,
        ArtifactRef = source.ArtifactRef,
        ArtifactExpiresAt = source.ArtifactExpiresAt,
        DueAt = source.DueAt,
        Attempts = source.Attempts,
        MaxAttempts = source.MaxAttempts,
        Version = version,
        LeaseToken = source.LeaseToken,
        LeasedBy = source.LeasedBy,
        LeasedUntil = source.LeasedUntil,
        LastError = source.LastError,
        RetainPayloadUntil = source.RetainPayloadUntil,
        CreatedAt = source.CreatedAt,
        UpdatedAt = source.UpdatedAt,
        CompletedAt = source.CompletedAt,
    };

    private static void UpdateMaximum(ref int maximum, int value)
    {
        while (true)
        {
            var observed = Volatile.Read(ref maximum);
            if (value <= observed
                || Interlocked.CompareExchange(ref maximum, value, observed) == observed)
                return;
        }
    }

    private sealed class DelegateHandler(
        string kind,
        Func<StateWorkItem, CancellationToken, Task<DurableWorkExecutionResult>> execute)
        : IDurableWorkItemHandler
    {
        public string Kind { get; } = kind;

        public Task<DurableWorkExecutionResult> ExecuteAsync(
            StateWorkItem item,
            CancellationToken ct) => execute(item, ct);
    }

    private sealed class FakeStateWorkClient : IStateWorkItemClient
    {
        private readonly IReadOnlyList<StateWorkItem> _claimed;

        public FakeStateWorkClient(IReadOnlyList<StateWorkItem> claimed) =>
            _claimed = claimed;

        public List<StateWorkClaim> Claims { get; } = [];
        public ConcurrentBag<RenewCall> Renewals { get; } = [];
        public ConcurrentBag<CompleteCall> Completes { get; } = [];
        public ConcurrentBag<DeferCall> Deferrals { get; } = [];
        public ConcurrentBag<FailCall> Failures { get; } = [];

        public Func<Guid, StateWorkLeaseRenew, CancellationToken, Task<StateWorkItem>>? OnRenew { get; set; }

        public Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
            StateWorkClaim request,
            CancellationToken ct)
        {
            Claims.Add(request);
            return Task.FromResult(_claimed);
        }

        public async Task<StateWorkItem> RenewLeaseAsync(
            Guid workItemId,
            StateWorkLeaseRenew request,
            CancellationToken ct)
        {
            Renewals.Add(new RenewCall(workItemId, request));
            if (OnRenew is not null)
                return await OnRenew(workItemId, request, ct);
            var source = _claimed.Single(item => item.WorkItemId == workItemId);
            return Clone(source, request.ExpectedVersion + 1);
        }

        public Task<StateWorkItem> CompleteAsync(
            Guid workItemId,
            StateWorkComplete request,
            CancellationToken ct)
        {
            var item = _claimed.Single(value => value.WorkItemId == workItemId);
            Completes.Add(new CompleteCall(item, request));
            return Task.FromResult(Clone(item, request.ExpectedVersion + 1));
        }

        public Task<StateWorkItem> DeferAsync(
            Guid workItemId,
            StateWorkDefer request,
            CancellationToken ct)
        {
            var item = _claimed.Single(value => value.WorkItemId == workItemId);
            Deferrals.Add(new DeferCall(item, request));
            return Task.FromResult(Clone(item, request.ExpectedVersion + 1));
        }

        public Task<StateWorkItem> FailAsync(
            Guid workItemId,
            StateWorkFail request,
            CancellationToken ct)
        {
            var item = _claimed.Single(value => value.WorkItemId == workItemId);
            Failures.Add(new FailCall(item, request));
            return Task.FromResult(Clone(item, request.ExpectedVersion + 1));
        }

        public Task<StateWorkItem> CreateAsync(
            string idempotencyKey,
            StateWorkItemCreate request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StateWorkItem?> GetLatestAsync(
            string application,
            string kind,
            string subjectRef,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> ConsumeAsync(
            Guid workItemId,
            StateWorkConsume request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record RenewCall(Guid WorkItemId, StateWorkLeaseRenew Request);
    private sealed record CompleteCall(StateWorkItem Item, StateWorkComplete Request);
    private sealed record DeferCall(StateWorkItem Item, StateWorkDefer Request);
    private sealed record FailCall(StateWorkItem Item, StateWorkFail Request);
}
