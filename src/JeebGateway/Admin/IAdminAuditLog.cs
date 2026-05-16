namespace JeebGateway.Admin;

/// <summary>
/// Append-only audit log of admin mutations. The MVP implementation is
/// in-memory; production wiring INSERTs into <c>admin_actions</c>
/// (db/migrations/0005) on the same transaction as the mutation so the
/// audit trail can never diverge from the entity state. T-backend-030.
/// </summary>
public interface IAdminAuditLog
{
    Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct);

    /// <summary>
    /// Returns the audit timeline for a single entity, newest first.
    /// Used by tests to assert that an admin mutation was recorded;
    /// production callers will read from <c>admin_actions</c> directly.
    /// </summary>
    Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
        string entityType, string entityId, CancellationToken ct);
}

public class AdminAuditAppend
{
    public required string AdminUserId { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public IReadOnlyDictionary<string, object?>? BeforeState { get; init; }
    public IReadOnlyDictionary<string, object?>? AfterState { get; init; }
    public string? RequestId { get; init; }
}
