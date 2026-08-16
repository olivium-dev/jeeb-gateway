using System.Text.Json;
using FluentAssertions;
using JeebGateway.Artifacts;
using JeebGateway.Infrastructure;
using JeebGateway.Jobs;
using JeebGateway.Requests;
using JeebGateway.StateService.Work;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class DurableWorkHandlerIdempotencyTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AccountDeletion_Legacy_Hash_Field_Is_Read_Only_During_State_Cutover()
    {
        var legacy = WorkItem(
            DurableWorkContract.AccountDeletionKind,
            JsonSerializer.SerializeToElement(new
            {
                userId = "legacy-user",
                anonymizedUserHash = "legacy-delivery-hash",
                hadActiveDeliveryAtRequest = false
            }),
            DateTimeOffset.Parse("2026-08-10T12:00:00Z"));

        var parsed = AccountDeletionWorkHandler.DeserializePayload(legacy);

        parsed.Should().NotBeNull();
        parsed!.EffectiveDeliveryAnonymizedUserHash.Should().Be("legacy-delivery-hash");
        var newWire = JsonSerializer.Serialize(
            new AccountDeletionWorkPayload("new-user", "new-delivery-hash", false),
            WebJson);
        newWire.Should().Contain("\"deliveryAnonymizedUserHash\":\"new-delivery-hash\"");
        newWire.Should().NotContain("\"anonymizedUserHash\"");
    }

    [Fact]
    public async Task AccountDeletion_Replay_Repeats_Only_Idempotent_Owner_Commands()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var clock = new FakeTimeProvider(now);
        var userId = Guid.NewGuid().ToString("D");
        var hash = AccountDeletionPolicy.HashUserId(userId);
        var users = new InMemoryUsersStore();
        await users.GetOrCreateAsync(userId, CancellationToken.None);
        await users.UpdateProfileAsync(
            userId,
            new ProfilePatch { Name = "PII Name", Email = "pii@example.test" },
            CancellationToken.None);
        var requests = new InMemoryRequestsStore(clock);
        var tokens = new CountingTokenService();
        var ledger = new CountingLedgerOwner();
        var handler = new AccountDeletionWorkHandler(
            users,
            requests,
            tokens,
            ledger,
            clock,
            Options.Create(new AccountDeletionExecutionOptions()));
        var item = WorkItem(
            DurableWorkContract.AccountDeletionKind,
            JsonSerializer.SerializeToElement(
                new AccountDeletionWorkPayload(userId, hash, false),
                WebJson),
            now - TimeSpan.FromDays(31),
            dueAt: now - TimeSpan.FromDays(1),
            lastError: AccountDeletionWorkHandler.PurgeScheduledMarker);

        var first = await handler.ExecuteAsync(item, CancellationToken.None);
        var crashReplay = await handler.ExecuteAsync(item, CancellationToken.None);

        first.Outcome.Should().Be(DurableWorkOutcome.Complete);
        crashReplay.Outcome.Should().Be(DurableWorkOutcome.Complete);
        tokens.Revocations.Should().Be(2);
        ledger.Closures.Should().Be(2);
        ledger.ReceivedPseudonyms.Should().OnlyContain(value => value == string.Empty,
            "the wallet owner chooses its own pseudonym");
        var purged = await users.GetByIdAsync(userId, CancellationToken.None);
        purged!.Name.Should().BeEmpty();
        purged.Email.Should().BeNull();
    }

    [Fact]
    public async Task AccountDeletion_Purge_Grace_Period_Is_An_Expected_Defer_Even_At_Attempt_Cap()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var purgeAt = now + AccountDeletionPolicy.PurgeDelay;
        var clock = new FakeTimeProvider(now);
        var userId = Guid.NewGuid().ToString("D");
        var users = new InMemoryUsersStore();
        await users.GetOrCreateAsync(userId, CancellationToken.None);
        var handler = new AccountDeletionWorkHandler(
            users,
            new InMemoryRequestsStore(clock),
            new CountingTokenService(),
            new CountingLedgerOwner(),
            clock,
            Options.Create(new AccountDeletionExecutionOptions()));
        var item = WorkItem(
            DurableWorkContract.AccountDeletionKind,
            JsonSerializer.SerializeToElement(
                new AccountDeletionWorkPayload(
                    userId,
                    AccountDeletionPolicy.HashUserId(userId),
                    false),
                WebJson),
            now,
            dueAt: purgeAt,
            lastError: AccountDeletionWorkHandler.PurgeScheduledMarker,
            attempts: 100,
            maxAttempts: 100);

        var result = await handler.ExecuteAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(DurableWorkOutcome.Defer);
        result.Error.Should().Be(AccountDeletionWorkHandler.PurgeScheduledMarker);
        result.RetryAt.Should().Be(purgeAt);
        (await users.GetByIdAsync(userId, CancellationToken.None)).Should().NotBeNull(
            "PII purge must wait for the grace deadline");
    }

    [Fact]
    public async Task DataExport_Replay_Reuses_Artifact_Notification_And_Capability_Identities()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var clock = new FakeTimeProvider(now);
        var workId = Guid.NewGuid();
        var packager = new CountingPackager();
        var notifier = new CountingNotifier();
        var artifacts = new RecordingArtifacts();
        var tokens = new FixedExportTokens(workId);
        var handler = new DataExportWorkHandler(
            packager,
            notifier,
            artifacts,
            tokens,
            Options.Create(new DataExportOptions()),
            clock);
        var item = WorkItem(
            DurableWorkContract.DataExportKind,
            JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-42", DataExportFormat.Json),
                WebJson),
            now,
            workItemId: workId);

        var first = await handler.ExecuteAsync(item, CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var crashReplay = await handler.ExecuteAsync(item, CancellationToken.None);

        first.Outcome.Should().Be(DurableWorkOutcome.Complete);
        crashReplay.Outcome.Should().Be(first.Outcome);
        crashReplay.ArtifactRef.Should().Be(first.ArtifactRef);
        crashReplay.ArtifactExpiresAt.Should().Be(first.ArtifactExpiresAt);
        crashReplay.DownloadTokenHash.Should().Be(first.DownloadTokenHash);
        first.Result.HasValue.Should().BeTrue();
        crashReplay.Result.HasValue.Should().BeTrue();
        crashReplay.Result!.Value.GetRawText().Should().Be(first.Result!.Value.GetRawText());
        artifacts.Recoveries.Should().Equal(
            $"data-export:{workId:N}",
            $"data-export:{workId:N}");
        var put = artifacts.Puts.Should().ContainSingle().Subject;
        put.IdempotencyKey.Should().Be($"data-export:{workId:N}");
        put.OwnerRef.Should().Be(item.SubjectRef);
        put.FileName.Should().Be("jeeb-data-export-user-42-20260810-120000.json");
        put.ContentType.Should().Be("application/json");
        put.RequestedExpiry.Should().Be(
            now + TimeSpan.FromDays(7));
        notifier.Calls.Select(call => (call.ExportId, call.Token, call.LinkExpiresAt)).Distinct()
            .Should().ContainSingle().Which.Should().Be((
                workId.ToString("D"),
                tokens.Capability.Token,
                put.RequestedExpiry));
        packager.Builds.Should().Be(1,
            "a completed owner upload is recovered before mutable export sources are read again");
        packager.GeneratedAt.Should().ContainSingle().Which.Should().Be(now);
    }

    [Fact]
    public async Task DataExport_Recovers_Ambiguous_Upload_Before_Sla_Or_Source_Reads()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var workId = Guid.NewGuid();
        var recovered = new PrivateArtifact(
            "artifact-recovered",
            now + TimeSpan.FromDays(1),
            41);
        var artifacts = new RecordingArtifacts(recovered);
        var packager = new CountingPackager();
        var notifier = new CountingNotifier();
        var tokens = new FixedExportTokens(workId);
        var handler = new DataExportWorkHandler(
            packager,
            notifier,
            artifacts,
            tokens,
            Options.Create(new DataExportOptions()),
            new FakeTimeProvider(now));
        var item = WorkItem(
            DurableWorkContract.DataExportKind,
            JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-42", DataExportFormat.Json),
                WebJson),
            now - TimeSpan.FromDays(4),
            workItemId: workId);

        var result = await handler.ExecuteAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(DurableWorkOutcome.Complete);
        result.ArtifactRef.Should().Be(recovered.ArtifactRef);
        result.ArtifactExpiresAt.Should().Be(recovered.ExpiresAt);
        result.Result!.Value.GetProperty("sizeBytes").GetInt64().Should().Be(41);
        packager.Builds.Should().Be(0,
            "recovery must run before the SLA gate and before mutable source reads");
        artifacts.Puts.Should().BeEmpty();
        artifacts.Recoveries.Should().ContainSingle()
            .Which.Should().Be($"data-export:{workId:N}");
    }

    [Fact]
    public async Task DataExport_Does_Not_Complete_With_An_Expired_Recovered_Artifact()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var workId = Guid.NewGuid();
        var artifacts = new RecordingArtifacts(new PrivateArtifact(
            "artifact-expired",
            now - TimeSpan.FromSeconds(1),
            41));
        var packager = new CountingPackager();
        var notifier = new CountingNotifier();
        var handler = new DataExportWorkHandler(
            packager,
            notifier,
            artifacts,
            new FixedExportTokens(workId),
            Options.Create(new DataExportOptions()),
            new FakeTimeProvider(now));
        var item = WorkItem(
            DurableWorkContract.DataExportKind,
            JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-42", DataExportFormat.Json),
                WebJson),
            now,
            workItemId: workId);

        var result = await handler.ExecuteAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(DurableWorkOutcome.Fail);
        result.Error.Should().Contain("expired");
        packager.Builds.Should().Be(0);
        artifacts.Puts.Should().BeEmpty();
        notifier.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task DataExport_Missing_Owner_Section_Defers_Without_Artifact_Or_Notification()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var workId = Guid.NewGuid();
        var artifacts = new RecordingArtifacts();
        var notifier = new CountingNotifier();
        var handler = new DataExportWorkHandler(
            new OwnerUnavailablePackager(),
            notifier,
            artifacts,
            new FixedExportTokens(workId),
            Options.Create(new DataExportOptions
            {
                SourceUnavailableRetryDelay = TimeSpan.FromMinutes(20),
            }),
            new FakeTimeProvider(now));
        var item = WorkItem(
            DurableWorkContract.DataExportKind,
            JsonSerializer.SerializeToElement(
                new DataExportWorkPayload("user-42", DataExportFormat.Json),
                WebJson),
            now,
            workItemId: workId);

        var result = await handler.ExecuteAsync(item, CancellationToken.None);

        result.Outcome.Should().Be(DurableWorkOutcome.Defer);
        result.RetryAt.Should().Be(now + TimeSpan.FromMinutes(20));
        result.Error.Should().Contain("chat-service complete per-user conversation");
        artifacts.Puts.Should().BeEmpty();
        notifier.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Chat_History_Provider_Never_Fabricates_An_Empty_Success()
    {
        var provider = new UnavailableChatConversationExportIndex();

        var act = () => provider.ListForViewerAsync(
            "user-42", null, null, 100, CancellationToken.None);

        await act.Should().ThrowAsync<OwnerCapabilityUnavailableException>()
            .WithMessage("*api/conversations/export-index*");
    }

    [Fact]
    public async Task Feedback_Ratings_Provider_Never_Omits_Authored_Ratings()
    {
        var provider = new FeedbackServiceDataExportRatingsProvider();

        var act = () => provider.GetForUserAsync("user-42", CancellationToken.None);

        await act.Should().ThrowAsync<OwnerCapabilityUnavailableException>()
            .WithMessage("*given and received*request correlation*");
    }

    private static StateWorkItem WorkItem(
        string kind,
        JsonElement payload,
        DateTimeOffset createdAt,
        DateTimeOffset? dueAt = null,
        string? lastError = null,
        Guid? workItemId = null,
        int attempts = 1,
        int maxAttempts = 100) => new()
    {
        WorkItemId = workItemId ?? Guid.NewGuid(),
        Application = DurableWorkContract.Application,
        Kind = kind,
        SubjectRef = "sha256:subject",
        Status = "leased",
        Payload = payload,
        DueAt = dueAt ?? createdAt,
        Attempts = attempts,
        MaxAttempts = maxAttempts,
        Version = 3,
        LeaseToken = Guid.NewGuid(),
        LastError = lastError,
        CreatedAt = createdAt,
        UpdatedAt = createdAt,
    };

    private sealed class CountingTokenService : ITokenService
    {
        public int Revocations { get; private set; }

        public Task<int> RevokeAllForUserAsync(
            string userId,
            RevocationReason reason,
            CancellationToken ct)
        {
            Revocations++;
            return Task.FromResult(1);
        }

        public Task<TokenPair> IssueAsync(
            string userId,
            IEnumerable<string> roles,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task RevokeAsync(
            string refreshToken,
            RevocationReason reason,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class CountingLedgerOwner : IFinancialLedgerAnonymizer
    {
        public int Closures { get; private set; }
        public List<string> ReceivedPseudonyms { get; } = [];

        public Task<int> AnonymizeForUserAsync(
            string userId,
            string anonymizedHash,
            CancellationToken ct)
        {
            Closures++;
            ReceivedPseudonyms.Add(anonymizedHash);
            return Task.FromResult(0);
        }

        public Task<int> CountRowsForUserAsync(string userId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<int> CountRowsForHashAsync(string anonymizedHash, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CountingPackager : IDataExportPackager
    {
        public int Builds { get; private set; }
        public List<DateTimeOffset> GeneratedAt { get; } = [];

        public Task<DataExportPayload> BuildAsync(
            string userId,
            string format,
            DateTimeOffset generatedAt,
            CancellationToken ct)
        {
            Builds++;
            GeneratedAt.Add(generatedAt);
            return Task.FromResult(new DataExportPayload
            {
                Bytes = [1, 2, 3],
                ContentType = "application/json",
                FileName = "export.json",
            });
        }
    }

    private sealed class OwnerUnavailablePackager : IDataExportPackager
    {
        public Task<DataExportPayload> BuildAsync(
            string userId,
            string format,
            DateTimeOffset generatedAt,
            CancellationToken ct) =>
            Task.FromException<DataExportPayload>(
                new OwnerCapabilityUnavailableException(
                    "chat-service complete per-user conversation and message export"));
    }

    private sealed class CountingNotifier : IDataExportNotifier
    {
        public List<NotificationCall> Calls { get; } = [];

        public Task NotifyReadyAsync(
            string userId,
            string exportId,
            string downloadToken,
            DateTimeOffset linkExpiresAt,
            CancellationToken ct)
        {
            Calls.Add(new NotificationCall(exportId, downloadToken, linkExpiresAt));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingArtifacts(PrivateArtifact? recovered = null)
        : IPrivateArtifactStore
    {
        private PrivateArtifact? _stored = recovered;
        public List<string> Recoveries { get; } = [];
        public List<ArtifactPut> Puts { get; } = [];

        public Task<PrivateArtifact?> RecoverUploadAsync(
            string idempotencyKey,
            CancellationToken ct)
        {
            Recoveries.Add(idempotencyKey);
            return Task.FromResult(_stored);
        }

        public Task<PrivateArtifact> PutAsync(
            string idempotencyKey,
            string ownerRef,
            string fileName,
            string contentType,
            byte[] content,
            DateTimeOffset requestedExpiry,
            CancellationToken ct)
        {
            Puts.Add(new ArtifactPut(
                idempotencyKey,
                ownerRef,
                fileName,
                contentType,
                requestedExpiry));
            _stored = new PrivateArtifact(
                "artifact-opaque",
                requestedExpiry,
                content.Length);
            return Task.FromResult(_stored);
        }

        public Task<PrivateArtifactDownload> CreateDownloadUrlAsync(
            string artifactRef,
            TimeSpan validity,
            bool singleUse,
            CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(string artifactRef, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class FixedExportTokens(Guid workId) : IDataExportTokenProtector
    {
        public DataExportCapability Capability { get; } = new(
            workId,
            $"v1.{workId:N}.fixed",
            "sha256:fixed");

        public DataExportCapability Create(Guid workItemId)
        {
            workItemId.Should().Be(workId);
            return Capability;
        }

        public bool TryValidate(string token, out DataExportCapability capability)
        {
            capability = Capability;
            return string.Equals(token, Capability.Token, StringComparison.Ordinal);
        }
    }

    private sealed record NotificationCall(
        string ExportId,
        string Token,
        DateTimeOffset LinkExpiresAt);
    private sealed record ArtifactPut(
        string IdempotencyKey,
        string OwnerRef,
        string FileName,
        string ContentType,
        DateTimeOffset RequestedExpiry);
}
