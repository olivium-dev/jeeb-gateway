namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Gateway projection over the prohibited-items catalog and per-user
/// acknowledgment ledger owned by ban-service. Runtime wiring is stateless;
/// the local implementations remain only as isolated test fixtures.
/// </summary>
public interface IProhibitedItemsStore
{
    Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct);

    /// <summary>
    /// Returns an immutable active catalog snapshot and its exact opaque owner
    /// version. The default keeps legacy test stores source-compatible; the
    /// ban-service adapter overrides it with a version-pinned owner read.
    /// </summary>
    async Task<ProhibitedCatalogSnapshot> GetActiveCatalogAsync(CancellationToken ct)
    {
        var items = await ListActiveAsync(ct);
        var version = items.Count == 0
            ? "empty"
            : items.Max(item => item.UpdatedAt).ToUniversalTime().ToString("O");
        return new ProhibitedCatalogSnapshot(items, version);
    }

    Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct);

    Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct);

    Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct);

    Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct);

    Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Reads the acknowledgement for one exact opaque catalog version. Owner
    /// adapters must not substitute a numeric storage revision or a newer tag.
    /// </summary>
    async Task<UserAcknowledgment?> GetAcknowledgmentAsync(
        string userId,
        string version,
        CancellationToken ct)
    {
        var acknowledgement = await GetAcknowledgmentAsync(userId, ct);
        return acknowledgement is not null
               && string.Equals(acknowledgement.Version, version, StringComparison.Ordinal)
            ? acknowledgement
            : null;
    }

    Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct);

    /// <summary>
    /// gwdbx W3-03 — enumerates the ack ledger (newest ack per user) so the freeze-import
    /// can replay it upstream. Read-only; no live caller outside the importer.
    /// </summary>
    Task<UserAcknowledgmentPage> ListAcknowledgmentsAsync(int page, int pageSize, CancellationToken ct);
}

public class UserAcknowledgmentPage
{
    public required IReadOnlyList<UserAcknowledgment> Items { get; init; }
    public required int Total { get; init; }
}

public sealed record ProhibitedCatalogSnapshot(
    IReadOnlyList<ProhibitedItem> Items,
    string Version);

public class ProhibitedItemCreate
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public string? Description { get; init; }

    /// <summary>
    /// JEB-63 moderation severity. Additive — defaults to
    /// <see cref="ProhibitedSeverity.Block"/> so an admin create that omits it
    /// keeps the stricter hard-reject behaviour.
    /// </summary>
    public ProhibitedSeverity Severity { get; init; } = ProhibitedSeverity.Block;
}

public class ProhibitedItemPatch
{
    public string? Name { get; init; }
    public string? Category { get; init; }
    public string? Description { get; init; }

    /// <summary>JEB-63 moderation severity (null = leave unchanged). Additive.</summary>
    public ProhibitedSeverity? Severity { get; init; }
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

/// <summary>
/// ban-service rejected a catalog mutation because the owner state changed.
/// This is deliberately distinct from a duplicate-name conflict so callers do
/// not mislabel every owner-side 409 as a uniqueness violation.
/// </summary>
public class ProhibitedCatalogConflictException : Exception
{
    public ProhibitedCatalogConflictException(string message)
        : base(message) { }
}

/// <summary>
/// The immutable catalog tag supplied to the atomic acknowledgement write was
/// no longer current when ban-service committed the request.
/// </summary>
public sealed class StaleProhibitedCatalogVersionException
    : ProhibitedCatalogConflictException
{
    public StaleProhibitedCatalogVersionException(string version, string message)
        : base(message)
    {
        Version = version;
    }

    public string Version { get; }
}
