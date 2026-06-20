using System.Collections.Concurrent;

namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// In-memory, per-user saved-location store (ACCT-04 / REQ-02). Thread-safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by userId, with a
/// per-user lock to keep the "exactly one default" invariant (REQ-02) atomic.
/// Mirrors <c>InMemoryNotificationPreferencesStore</c>: clones on the way out so
/// callers cannot mutate stored state.
/// </summary>
public class InMemorySavedLocationStore : ISavedLocationStore
{
    private readonly ConcurrentDictionary<string, Dictionary<string, SavedLocation>> _byUser = new();

    public Task<IReadOnlyList<SavedLocation>> ListAsync(string userId, CancellationToken ct)
    {
        var bucket = _byUser.GetOrAdd(userId, _ => new());
        lock (bucket)
        {
            IReadOnlyList<SavedLocation> items = bucket.Values
                .OrderByDescending(l => l.IsDefault)
                .ThenBy(l => l.CreatedAt)
                .Select(Clone)
                .ToList();
            return Task.FromResult(items);
        }
    }

    public Task<SavedLocation?> GetAsync(string userId, string id, CancellationToken ct)
    {
        var bucket = _byUser.GetOrAdd(userId, _ => new());
        lock (bucket)
        {
            return Task.FromResult(bucket.TryGetValue(id, out var found) ? Clone(found) : null);
        }
    }

    public Task<SavedLocation> CreateAsync(string userId, CreateSavedLocationRequest request, CancellationToken ct)
    {
        var bucket = _byUser.GetOrAdd(userId, _ => new());
        lock (bucket)
        {
            var now = DateTimeOffset.UtcNow;
            // REQ-02: first saved location is the implicit default if caller did not ask.
            var makeDefault = request.IsDefault || bucket.Count == 0;

            var created = new SavedLocation
            {
                Id = Guid.NewGuid().ToString("N"),
                UserId = userId,
                Label = request.Label,
                Address = request.Address,
                Latitude = request.Latitude,
                Longitude = request.Longitude,
                IsDefault = makeDefault,
                CreatedAt = now,
                UpdatedAt = now
            };

            if (makeDefault) ClearDefaults(bucket);
            bucket[created.Id] = created;
            return Task.FromResult(Clone(created));
        }
    }

    public Task<SavedLocation?> UpdateAsync(string userId, string id, UpdateSavedLocationRequest request, CancellationToken ct)
    {
        var bucket = _byUser.GetOrAdd(userId, _ => new());
        lock (bucket)
        {
            if (!bucket.TryGetValue(id, out var existing))
                return Task.FromResult<SavedLocation?>(null);

            if (request.Label is { } label) existing.Label = label;
            if (request.Address is { } address) existing.Address = address;
            if (request.Latitude is { } lat) existing.Latitude = lat;
            if (request.Longitude is { } lng) existing.Longitude = lng;

            if (request.IsDefault is true)
            {
                ClearDefaults(bucket);
                existing.IsDefault = true;
            }
            else if (request.IsDefault is false)
            {
                existing.IsDefault = false;
            }

            existing.UpdatedAt = DateTimeOffset.UtcNow;
            return Task.FromResult<SavedLocation?>(Clone(existing));
        }
    }

    public Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        var bucket = _byUser.GetOrAdd(userId, _ => new());
        lock (bucket)
        {
            if (!bucket.Remove(id, out var removed))
                return Task.FromResult(false);

            // REQ-02: if we removed the default, promote the oldest remaining so the
            // user always has a "my location" while any saved location exists.
            if (removed.IsDefault)
            {
                var next = bucket.Values.OrderBy(l => l.CreatedAt).FirstOrDefault();
                if (next is not null)
                {
                    next.IsDefault = true;
                    next.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            return Task.FromResult(true);
        }
    }

    private static void ClearDefaults(Dictionary<string, SavedLocation> bucket)
    {
        foreach (var loc in bucket.Values)
            loc.IsDefault = false;
    }

    private static SavedLocation Clone(SavedLocation l) => new()
    {
        Id = l.Id,
        UserId = l.UserId,
        Label = l.Label,
        Address = l.Address,
        Latitude = l.Latitude,
        Longitude = l.Longitude,
        IsDefault = l.IsDefault,
        CreatedAt = l.CreatedAt,
        UpdatedAt = l.UpdatedAt
    };
}
