namespace JeebGateway.Admin;

/// <summary>
/// One audit row recording an admin mutation. Mirrors the
/// <c>admin_actions</c> table introduced in db/migrations/0005 — the
/// in-memory store keeps the same field shape so the production swap
/// reduces to a different IAdminAuditLog implementation. T-backend-030.
/// </summary>
public class AdminAuditEntry
{
    public required string Id { get; init; }
    public required string AdminUserId { get; init; }
    public required string Action { get; init; }
    public required string EntityType { get; init; }
    public string? EntityId { get; init; }
    public IReadOnlyDictionary<string, object?>? BeforeState { get; init; }
    public IReadOnlyDictionary<string, object?>? AfterState { get; init; }
    public string? RequestId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
