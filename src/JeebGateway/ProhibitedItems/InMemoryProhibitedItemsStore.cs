using System.Collections.Concurrent;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// MVP-grade in-memory catalog + ack ledger. Mirrors the constraints encoded
/// in db/migrations/0005:
///   * case-insensitive uniqueness on Name
///   * Active is a soft-delete flag; inactive rows are kept so the audit log
///     and any historical references stay resolvable
///
/// All public methods are safe under concurrent access; mutations on the items
/// map happen under <see cref="_writeLock"/> so the uniqueness check and the
/// insert/update form a single critical section.
/// </summary>
public class InMemoryProhibitedItemsStore : IProhibitedItemsStore
{
    private readonly ConcurrentDictionary<string, ProhibitedItem> _items = new();
    private readonly ConcurrentDictionary<string, UserAcknowledgment> _acks = new();
    private readonly object _writeLock = new();

    public Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
    {
        IReadOnlyList<ProhibitedItem> list = _items.Values
            .Where(i => i.Active)
            .OrderBy(i => i.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Clone)
            .ToList();

        return Task.FromResult(list);
    }

    public Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
    {
        var ordered = _items.Values
            .OrderByDescending(i => i.UpdatedAt)
            .ToList();

        var skip = (page - 1) * pageSize;
        var items = ordered.Skip(skip).Take(pageSize).Select(Clone).ToList();

        return Task.FromResult(new ProhibitedItemsPage
        {
            Items = items,
            Total = ordered.Count
        });
    }

    public Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
    {
        if (_items.TryGetValue(id, out var item))
        {
            return Task.FromResult<ProhibitedItem?>(Clone(item));
        }

        return Task.FromResult<ProhibitedItem?>(null);
    }

    public Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new ProhibitedItem
        {
            Id = Guid.NewGuid().ToString(),
            Name = input.Name.Trim(),
            Category = input.Category,
            Description = input.Description,
            Active = true,
            CreatedBy = adminUserId,
            UpdatedBy = adminUserId,
            CreatedAt = now,
            UpdatedAt = now
        };

        lock (_writeLock)
        {
            if (HasActiveNameConflict(item.Name, excludingId: null))
            {
                throw new DuplicateProhibitedItemNameException(item.Name);
            }

            _items[item.Id] = item;
        }

        return Task.FromResult(Clone(item));
    }

    public Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_items.TryGetValue(id, out var existing))
            {
                return Task.FromResult<ProhibitedItem?>(null);
            }

            if (patch.Name is { } newName)
            {
                var trimmed = newName.Trim();
                if (HasActiveNameConflict(trimmed, excludingId: id))
                {
                    throw new DuplicateProhibitedItemNameException(trimmed);
                }
                existing.Name = trimmed;
            }

            if (patch.Category is { } category) existing.Category = category;
            if (patch.Description is not null) existing.Description = patch.Description;
            if (patch.Active is { } active) existing.Active = active;

            existing.UpdatedBy = adminUserId;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            return Task.FromResult<ProhibitedItem?>(Clone(existing));
        }
    }

    public Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct)
    {
        _acks.TryGetValue(userId, out var ack);
        return Task.FromResult(ack);
    }

    public Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct)
    {
        var ack = new UserAcknowledgment
        {
            UserId = userId,
            Version = version,
            AcknowledgedAt = DateTimeOffset.UtcNow
        };

        _acks[userId] = ack;
        return Task.FromResult(ack);
    }

    private bool HasActiveNameConflict(string name, string? excludingId)
    {
        foreach (var i in _items.Values)
        {
            if (excludingId is not null && string.Equals(i.Id, excludingId, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static ProhibitedItem Clone(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Active = i.Active,
        CreatedBy = i.CreatedBy,
        UpdatedBy = i.UpdatedBy,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt
    };
}
