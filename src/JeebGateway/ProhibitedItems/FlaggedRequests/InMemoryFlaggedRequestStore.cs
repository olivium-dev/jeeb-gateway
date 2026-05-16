using System.Collections.Concurrent;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

public class InMemoryFlaggedRequestStore : IFlaggedRequestStore
{
    private readonly ConcurrentDictionary<string, FlaggedRequest> _items = new();

    public Task<FlaggedRequest> CreateAsync(FlaggedRequestCreate input, CancellationToken ct)
    {
        var record = new FlaggedRequest
        {
            Id = Guid.NewGuid().ToString(),
            RequestId = input.RequestId,
            UserId = input.UserId,
            Description = input.Description,
            Matches = input.Matches,
            Status = FlaggedRequestStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow
        };

        _items[record.Id] = record;
        return Task.FromResult(Clone(record));
    }

    public Task<FlaggedRequest?> GetAsync(string id, CancellationToken ct)
    {
        _items.TryGetValue(id, out var item);
        return Task.FromResult(item is null ? null : Clone(item));
    }

    public Task<FlaggedRequestPage> ListAsync(
        FlaggedRequestStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _items.Values.AsEnumerable();
        if (status is { } s) query = query.Where(f => f.Status == s);

        var ordered = query
            .OrderByDescending(f => f.CreatedAt)
            .ToList();

        var skip = (page - 1) * pageSize;
        var slice = ordered.Skip(skip).Take(pageSize).Select(Clone).ToList();

        return Task.FromResult(new FlaggedRequestPage
        {
            Items = slice,
            Total = ordered.Count
        });
    }

    public Task<FlaggedRequest?> DecideAsync(
        string id,
        FlaggedRequestStatus status,
        string adminUserId,
        string? note,
        CancellationToken ct)
    {
        if (!_items.TryGetValue(id, out var existing))
        {
            return Task.FromResult<FlaggedRequest?>(null);
        }

        existing.Status = status;
        existing.DecidedBy = adminUserId;
        existing.DecidedAt = DateTimeOffset.UtcNow;
        existing.DecisionNote = note;

        return Task.FromResult<FlaggedRequest?>(Clone(existing));
    }

    private static FlaggedRequest Clone(FlaggedRequest source) => new()
    {
        Id = source.Id,
        RequestId = source.RequestId,
        UserId = source.UserId,
        Description = source.Description,
        Matches = source.Matches.ToList(),
        Status = source.Status,
        CreatedAt = source.CreatedAt,
        DecidedBy = source.DecidedBy,
        DecidedAt = source.DecidedAt,
        DecisionNote = source.DecisionNote
    };
}
