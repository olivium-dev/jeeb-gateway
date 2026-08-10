using System.Text.Json;
using FluentAssertions;
using JeebGateway.Cases;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.ProhibitedItems.FlaggedRequests;

public sealed class StateServiceFlaggedRequestStoreTests
{
    [Fact]
    public async Task Lifecycle_is_owned_by_generic_case_api()
    {
        var owner = new FakeCaseClient();
        var store = new StateServiceFlaggedRequestStore(owner);

        var created = await store.CreateAsync(NewCreate(), default);
        created.Status.Should().Be(FlaggedRequestStatus.Pending);
        created.RequestId.Should().Be("request-1");
        created.Matches.Should().ContainSingle().Which.Severity.Should().Be(ProhibitedSeverity.Block);
        owner.LastCreate!.Kind.Should().Be("moderation_review");
        owner.LastCreate.Category.Should().Be("prohibited_item");
        owner.LastCreateKey.Should().StartWith("jeeb:flagged:create:");
        owner.Messages.Should().ContainSingle(message => message.MessageType == "internal_note");

        var decided = await store.DecideAsync(
            created.Id, FlaggedRequestStatus.Upheld, "admin-9", "confirmed", default);

        decided.Should().NotBeNull();
        decided!.Status.Should().Be(FlaggedRequestStatus.Upheld);
        decided.DecidedBy.Should().Be("admin-9");
        decided.DecisionNote.Should().Be("confirmed");
        owner.AtomicDecisionCalls.Should().Be(1);

        var fetched = await store.GetAsync(created.Id, default);
        fetched.Should().BeEquivalentTo(decided);
    }

    [Fact]
    public async Task Create_replay_does_not_append_duplicate_metadata()
    {
        var owner = new FakeCaseClient();
        var store = new StateServiceFlaggedRequestStore(owner);
        var input = NewCreate();

        var first = await store.CreateAsync(input, default);
        var second = await store.CreateAsync(input, default);

        second.Id.Should().Be(first.Id);
        owner.Messages.Count(message => message.MessageType == "internal_note").Should().Be(1);
    }

    [Fact]
    public async Task List_uses_owner_status_filter_and_returns_legacy_page_shape()
    {
        var owner = new FakeCaseClient();
        var store = new StateServiceFlaggedRequestStore(owner);
        var pending = await store.CreateAsync(NewCreate("request-pending"), default);
        var cleared = await store.CreateAsync(NewCreate("request-cleared"), default);
        await store.DecideAsync(cleared.Id, FlaggedRequestStatus.Cleared, "admin", null, default);

        var page = await store.ListAsync(FlaggedRequestStatus.Pending, 1, 20, default);

        owner.LastQuery!.Status.Should().Be("pending");
        page.Total.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(pending.Id);
    }

    [Fact]
    public async Task Malformed_or_foreign_ids_are_not_exposed()
    {
        var owner = new FakeCaseClient();
        var store = new StateServiceFlaggedRequestStore(owner);

        (await store.GetAsync("not-a-guid", default)).Should().BeNull();
        (await store.GetAsync(Guid.NewGuid().ToString(), default)).Should().BeNull();
    }

    private static FlaggedRequestCreate NewCreate(string requestId = "request-1") => new()
    {
        RequestId = requestId,
        UserId = "user-7",
        Description = "contains a prohibited item",
        Matches = new[]
        {
            new ProhibitedItemMatch
            {
                ItemId = Guid.NewGuid().ToString("D"),
                ItemName = "weapon",
                Category = "illegal",
                MatchedTerm = "weapon",
                Evidence = "weapon",
                MatchType = ProhibitedMatchType.Exact,
                Confidence = 1,
                Severity = ProhibitedSeverity.Block
            }
        }
    };

    private sealed class FakeCaseClient : IGenericCaseStateClient
    {
        private readonly Dictionary<Guid, GenericCaseV1> _cases = new();
        private readonly Dictionary<string, Guid> _creates = new(StringComparer.Ordinal);
        public CreateGenericCaseRequestV1? LastCreate { get; private set; }
        public string? LastCreateKey { get; private set; }
        public GenericCaseQueryV1? LastQuery { get; private set; }
        public int AtomicDecisionCalls { get; private set; }
        public List<GenericCaseMessageV1> Messages { get; } = new();

        public Task<GenericCaseV1> CreateCaseAsync(CreateGenericCaseRequestV1 body,
            string idempotencyKey, string actorRef, string actorRole, CancellationToken ct)
        {
            LastCreate = body;
            LastCreateKey = idempotencyKey;
            if (_creates.TryGetValue(idempotencyKey, out var existing))
                return Task.FromResult(_cases[existing]);
            var id = Guid.NewGuid();
            var row = Row(id, body, 1);
            _cases[id] = row;
            _creates[idempotencyKey] = id;
            return Task.FromResult(row);
        }

        public Task<GenericCaseV1> GetCaseAsync(Guid caseId, CancellationToken ct) =>
            _cases.TryGetValue(caseId, out var row)
                ? Task.FromResult(row)
                : Task.FromException<GenericCaseV1>(new GenericCaseApiException(404, null));

        public Task<GenericCasePageV1> ListCasesAsync(GenericCaseQueryV1 query, CancellationToken ct)
        {
            LastQuery = query;
            var rows = _cases.Values
                .Where(row => query.Kind is null || row.Kind == query.Kind)
                .Where(row => query.Status is null || row.Status == query.Status)
                .OrderByDescending(row => row.CreatedAt)
                .ToArray();
            return Task.FromResult(new GenericCasePageV1 { Items = rows });
        }

        public Task<GenericCaseV1> PatchCaseAsync(Guid caseId, PatchGenericCaseRequestV1 body,
            string idempotencyKey, string actorRef, string actorRole, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<GenericCaseStatusMessageV1> ApplyCaseStatusMessageAsync(Guid caseId,
            ApplyGenericCaseStatusMessageRequestV1 body, string idempotencyKey,
            string actorRef, string actorRole, CancellationToken ct)
        {
            AtomicDecisionCalls++;
            var current = _cases[caseId];
            var updated = Copy(current, body.Status, current.Version + 1);
            var message = Message(caseId, updated.Version, "message", body.Body, actorRef, actorRole);
            _cases[caseId] = updated;
            Messages.Add(message);
            return Task.FromResult(new GenericCaseStatusMessageV1 { Case = updated, Message = message });
        }

        public Task<GenericCaseMessageCreatedV1> AddCaseMessageAsync(Guid caseId,
            CreateGenericCaseMessageRequestV1 body, string idempotencyKey,
            string actorRef, string actorRole, CancellationToken ct)
        {
            var current = _cases[caseId];
            var version = current.Version + 1;
            var message = Message(caseId, version, body.MessageType, body.Body ?? string.Empty,
                actorRef, actorRole);
            Messages.Add(message);
            _cases[caseId] = Copy(current, current.Status, version);
            return Task.FromResult(new GenericCaseMessageCreatedV1
            {
                Message = message,
                CaseVersion = version
            });
        }

        public Task<IReadOnlyList<GenericCaseMessageV1>> GetCaseMessagesAsync(
            Guid caseId, bool includeInternal, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenericCaseMessageV1>>(Messages
                .Where(message => message.CaseId == caseId)
                .Where(message => includeInternal || message.MessageType != "internal_note")
                .OrderByDescending(message => message.CaseVersion)
                .ToArray());

        public Task<GenericCaseMessagePageV1> GetCaseMessagesPageAsync(
            Guid caseId, bool includeInternal, string order, int limit, string? cursor,
            CancellationToken ct) => Task.FromResult(new GenericCaseMessagePageV1
        {
            Items = Messages
                .Where(message => message.CaseId == caseId)
                .Where(message => includeInternal || message.MessageType != "internal_note")
                .OrderByDescending(message => message.CaseVersion)
                .Take(limit)
                .ToArray()
        });

        public Task<IReadOnlyList<GenericCaseAttachmentV1>> GetCaseAttachmentsAsync(
            Guid caseId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenericCaseAttachmentV1>>(Array.Empty<GenericCaseAttachmentV1>());

        public Task<IReadOnlyList<GenericCaseAuditEventV1>> GetCaseAuditAsync(
            Guid caseId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<GenericCaseAuditEventV1>>(Array.Empty<GenericCaseAuditEventV1>());

        public Task<GenericCaseDeadLetterPageV1> GetCaseDeadLettersAsync(
            int limit, string? cursor, CancellationToken ct) =>
            Task.FromResult(new GenericCaseDeadLetterPageV1());

        public Task<GenericCaseDeadLetterRequeueV1> RequeueCaseDeadLetterAsync(
            Guid eventId, string idempotencyKey, string actorRef, CancellationToken ct) =>
            Task.FromResult(new GenericCaseDeadLetterRequeueV1 { EventId = eventId });

        private static GenericCaseV1 Row(Guid id, CreateGenericCaseRequestV1 body, int version) => new()
        {
            CaseId = id,
            Kind = body.Kind,
            Category = body.Category,
            Subject = body.Subject,
            RequesterRef = body.RequesterRef,
            ParticipantRefs = body.ParticipantRefs,
            Status = body.Status,
            Priority = body.Priority,
            Version = version,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private static GenericCaseV1 Copy(GenericCaseV1 row, string status, int version) => new()
        {
            CaseId = row.CaseId,
            Kind = row.Kind,
            Category = row.Category,
            Subject = row.Subject,
            RequesterRef = row.RequesterRef,
            ParticipantRefs = row.ParticipantRefs,
            Status = status,
            Priority = row.Priority,
            AssigneeRef = row.AssigneeRef,
            DueAt = row.DueAt,
            Version = version,
            ClosedAt = row.ClosedAt,
            CreatedAt = row.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        private static GenericCaseMessageV1 Message(
            Guid caseId, int version, string type, string body, string actorRef, string actorRole) => new()
        {
            MessageId = Guid.NewGuid(),
            CaseId = caseId,
            MessageType = type,
            Body = body,
            Actor = new GenericCaseActorV1 { Ref = actorRef, Role = actorRole },
            CaseVersion = version,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }
}
