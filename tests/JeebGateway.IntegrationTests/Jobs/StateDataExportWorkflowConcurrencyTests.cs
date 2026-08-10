using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Artifacts;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class StateDataExportWorkflowConcurrencyTests
{
    [Fact]
    public async Task Concurrent_Redemption_Returns_Only_The_Atomic_Consume_Winner()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var workId = Guid.NewGuid();
        var item = new StateWorkItem
        {
            WorkItemId = workId,
            Application = DurableWorkContract.Application,
            Kind = DurableWorkContract.DataExportKind,
            SubjectRef = "sha256:subject",
            Status = "completed",
            Payload = JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-42", DataExportFormat.Json)),
            Result = JsonSerializer.SerializeToElement(new { sizeBytes = 3 }),
            ArtifactRef = "opaque-artifact",
            ArtifactExpiresAt = now + TimeSpan.FromDays(1),
            DueAt = now,
            Attempts = 1,
            MaxAttempts = 10,
            Version = 11,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        var state = new AtomicConsumeStateClient(item);
        var artifacts = new BarrierArtifactStore(now + TimeSpan.FromMinutes(5));
        var tokens = new FixedTokens(new DataExportCapability(
            workId,
            "v1.fixed.capability",
            "sha256:fixed-capability"));
        var workflow = new StateDataExportWorkflow(
            state,
            artifacts,
            tokens,
            Options.Create(new DataExportOptions()),
            new FakeTimeProvider(now));

        var results = await Task.WhenAll(
            workflow.RedeemDownloadAsync(tokens.Capability.Token, CancellationToken.None),
            workflow.RedeemDownloadAsync(tokens.Capability.Token, CancellationToken.None));

        results.Count(value => value is not null).Should().Be(1);
        results.Count(value => value is null).Should().Be(1);
        artifacts.Calls.Should().HaveCount(2,
            "both callers may mint an unexposed owner URL before the state CAS");
        artifacts.Calls.Should().OnlyContain(call =>
            call.ArtifactRef == "opaque-artifact"
            && call.Validity == TimeSpan.FromMinutes(5)
            && call.SingleUse);
        state.Consumes.Should().HaveCount(2);
        state.Consumes.Should().OnlyContain(call =>
            call.WorkItemId == workId
            && call.Request.Application == DurableWorkContract.Application
            && call.Request.DownloadTokenHash == tokens.Capability.TokenHash
            && call.Request.ExpectedVersion == 11);
    }

    private sealed class FixedTokens(DataExportCapability capability) : IDataExportTokenProtector
    {
        public DataExportCapability Capability { get; } = capability;

        public DataExportCapability Create(Guid workItemId) =>
            workItemId == Capability.WorkItemId
                ? Capability
                : throw new InvalidOperationException();

        public bool TryValidate(string token, out DataExportCapability result)
        {
            result = Capability;
            return string.Equals(token, Capability.Token, StringComparison.Ordinal);
        }
    }

    private sealed class BarrierArtifactStore(DateTimeOffset expiresAt) : IPrivateArtifactStore
    {
        private readonly TaskCompletionSource _bothMinting =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _mintCount;
        public ConcurrentBag<ArtifactCall> Calls { get; } = [];

        public Task<PrivateArtifact?> RecoverUploadAsync(
            string idempotencyKey,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<PrivateArtifact> PutAsync(
            string idempotencyKey,
            string ownerRef,
            string fileName,
            string contentType,
            byte[] content,
            DateTimeOffset requestedExpiry,
            CancellationToken ct) => throw new NotSupportedException();

        public async Task<PrivateArtifactDownload> CreateDownloadUrlAsync(
            string artifactRef,
            TimeSpan validity,
            bool singleUse,
            CancellationToken ct)
        {
            Calls.Add(new ArtifactCall(artifactRef, validity, singleUse));
            var ordinal = Interlocked.Increment(ref _mintCount);
            if (ordinal == 2)
                _bothMinting.TrySetResult();
            await _bothMinting.Task.WaitAsync(ct);
            return new PrivateArtifactDownload(
                new Uri($"https://download.example.test/{ordinal}"),
                expiresAt);
        }

        public Task DeleteAsync(string artifactRef, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class AtomicConsumeStateClient(StateWorkItem item) : IStateWorkItemClient
    {
        private int _consumed;
        public ConcurrentBag<ConsumeCall> Consumes { get; } = [];

        public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct) =>
            Task.FromResult<StateWorkItem?>(workItemId == item.WorkItemId ? item : null);

        public Task<StateWorkItem> ConsumeAsync(
            Guid workItemId,
            StateWorkConsume request,
            CancellationToken ct)
        {
            Consumes.Add(new ConsumeCall(workItemId, request));
            if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
                throw new StateWorkConflictException("consume");
            return Task.FromResult(item);
        }

        public Task<StateWorkItem> CreateAsync(
            string idempotencyKey,
            StateWorkItemCreate request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem?> GetLatestAsync(
            string application,
            string kind,
            string subjectRef,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
            StateWorkClaim request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> RenewLeaseAsync(
            Guid workItemId,
            StateWorkLeaseRenew request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> CompleteAsync(
            Guid workItemId,
            StateWorkComplete request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> DeferAsync(
            Guid workItemId,
            StateWorkDefer request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> FailAsync(
            Guid workItemId,
            StateWorkFail request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record ArtifactCall(string ArtifactRef, TimeSpan Validity, bool SingleUse);
    private sealed record ConsumeCall(Guid WorkItemId, StateWorkConsume Request);
}
