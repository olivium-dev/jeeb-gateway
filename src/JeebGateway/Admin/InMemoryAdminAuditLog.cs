using System.Collections.Concurrent;

namespace JeebGateway.Admin;

/// <summary>
/// In-memory append-only audit log. Production swap is a Postgres-backed
/// implementation writing to db/migrations/0005.admin_actions on the same
/// transaction as the mutation it records.
/// </summary>
public class InMemoryAdminAuditLog : IAdminAuditLog
{
    private readonly ConcurrentQueue<AdminAuditEntry> _entries = new();

    public Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct)
    {
        var row = new AdminAuditEntry
        {
            Id = Guid.NewGuid().ToString(),
            AdminUserId = entry.AdminUserId,
            Action = entry.Action,
            EntityType = entry.EntityType,
            EntityId = entry.EntityId,
            BeforeState = entry.BeforeState,
            AfterState = entry.AfterState,
            RequestId = entry.RequestId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _entries.Enqueue(row);
        return Task.FromResult(row);
    }

    public Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
        string entityType, string entityId, CancellationToken ct)
    {
        var matches = _entries
            .Where(e => e.EntityType == entityType && e.EntityId == entityId)
            .OrderByDescending(e => e.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<AdminAuditEntry>>(matches);
    }
}
