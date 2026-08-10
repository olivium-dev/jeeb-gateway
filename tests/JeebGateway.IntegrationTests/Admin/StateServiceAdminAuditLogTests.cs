using System.Text.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.StateService.Audit;
using Xunit;

namespace JeebGateway.IntegrationTests.Admin;

public sealed class StateServiceAdminAuditLogTests
{
    [Fact]
    public async Task Append_projects_product_fields_to_generic_owner_contract()
    {
        var owner = new RecordingAuditClient();
        var store = new StateServiceAdminAuditLog(owner);

        var result = await store.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = "admin-42",
            Action = "suspend_user",
            EntityType = "user",
            EntityId = "user-9",
            BeforeState = new Dictionary<string, object?> { ["suspended"] = false },
            AfterState = new Dictionary<string, object?> { ["suspended"] = true },
            RequestId = "request-17"
        }, default);

        owner.LastKey.Should().StartWith("jeeb:admin-audit:");
        owner.LastAppend.Should().BeEquivalentTo(new
        {
            Application = "jeeb-gateway",
            ActorRef = "admin-42",
            ActorRole = "admin",
            Action = "suspend_user",
            ResourceType = "user",
            ResourceRef = "user-9",
            RequestId = "request-17"
        });
        owner.LastAppend!.Before!.Value.GetProperty("suspended").GetBoolean().Should().BeFalse();
        result.Id.Should().Be(owner.EventId.ToString("D"));
        result.EntityId.Should().Be("user-9");
        result.AfterState.Should().ContainKey("suspended");
    }

    [Fact]
    public async Task List_exhausts_owner_cursors_and_preserves_newest_first_contract()
    {
        var owner = new RecordingAuditClient { PageResults = true };
        var store = new StateServiceAdminAuditLog(owner);

        var rows = await store.ListForEntityAsync("delivery", "delivery-1", default);

        owner.Queries.Should().HaveCount(2);
        owner.Queries.Should().OnlyContain(query =>
            query.Application == "jeeb-gateway"
            && query.ResourceType == "delivery"
            && query.ResourceRef == "delivery-1");
        rows.Select(row => row.Action).Should().Equal("newer", "older");
    }

    private sealed class RecordingAuditClient : IStateAuditClient
    {
        public Guid EventId { get; } = Guid.NewGuid();
        public string? LastKey { get; private set; }
        public StateAuditAppend? LastAppend { get; private set; }
        public List<StateAuditQuery> Queries { get; } = new();
        public bool PageResults { get; init; }

        public Task<StateAuditEvent> AppendAsync(
            string idempotencyKey,
            StateAuditAppend request,
            CancellationToken ct)
        {
            LastKey = idempotencyKey;
            LastAppend = request;
            return Task.FromResult(Row(EventId, request.Action, DateTimeOffset.Parse("2026-08-10T10:00:00Z"),
                request.Before, request.After, request.ActorRef, request.ResourceType,
                request.ResourceRef, request.RequestId));
        }

        public Task<StateAuditPage> FindAsync(StateAuditQuery query, CancellationToken ct)
        {
            Queries.Add(query);
            if (!PageResults) return Task.FromResult(new StateAuditPage());
            return Task.FromResult(query.Cursor is null
                ? new StateAuditPage
                {
                    Items = new[] { Row(Guid.NewGuid(), "older", DateTimeOffset.Parse("2026-08-10T08:00:00Z")) },
                    NextCursor = "next"
                }
                : new StateAuditPage
                {
                    Items = new[] { Row(Guid.NewGuid(), "newer", DateTimeOffset.Parse("2026-08-10T09:00:00Z")) }
                });
        }

        private static StateAuditEvent Row(
            Guid id,
            string action,
            DateTimeOffset at,
            JsonElement? before = null,
            JsonElement? after = null,
            string actor = "admin-1",
            string resourceType = "delivery",
            string resourceRef = "delivery-1",
            string? requestId = null) => new()
        {
            EventId = id,
            Application = "jeeb-gateway",
            ActorRef = actor,
            ActorRole = "admin",
            Action = action,
            ResourceType = resourceType,
            ResourceRef = resourceRef,
            RequestId = requestId,
            Before = before ?? JsonSerializer.SerializeToElement(new { }),
            After = after ?? JsonSerializer.SerializeToElement(new { }),
            Metadata = JsonSerializer.SerializeToElement(new { }),
            OccurredAt = at,
            CreatedAt = at
        };
    }
}
