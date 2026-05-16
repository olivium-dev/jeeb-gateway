namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Storage abstraction for the moderated prohibited-items catalog and per-user
/// acknowledgment ledger. The default in-memory implementation is intended for
/// early-MVP local runs and integration tests; production wiring will hit
/// Postgres directly using the schema in db/migrations/0005, with the ack
/// ledger added in a follow-up migration.
/// </summary>
public interface IProhibitedItemsStore
{
    Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct);

    Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct);

    Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct);

    Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct);

    Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct);

    Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct);

    Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct);
}

public class ProhibitedItemCreate
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }
}

public class ProhibitedItemPatch
{
    public string? Name { get; init; }
    public string? Category { get; init; }
    public string? Description { get; init; }
    public bool? Active { get; init; }
}

public class ProhibitedItemsPage
{
    public required IReadOnlyList<ProhibitedItem> Items { get; init; }
    public required int Total { get; init; }
}

public class UserAcknowledgment
{
    public required string UserId { get; init; }
    public required string Version { get; init; }
    public required DateTimeOffset AcknowledgedAt { get; init; }
}

public class DuplicateProhibitedItemNameException : Exception
{
    public DuplicateProhibitedItemNameException(string name)
        : base($"A prohibited item named '{name}' already exists.") { }
}
