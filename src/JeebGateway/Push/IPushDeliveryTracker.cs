using System.Collections.Concurrent;

namespace JeebGateway.Push;

/// <summary>
/// Records every push delivery outcome so the ops dashboard and downstream
/// analytics can answer "did this user receive the push?" and "what's the
/// retry-path save rate?".
///
/// In-memory for the MVP; production swap writes to an append-only
/// push_delivery_log table with a retention policy.
/// </summary>
public interface IPushDeliveryTracker
{
    Task RecordAsync(PushDeliveryResult result, CancellationToken ct);
    Task<IReadOnlyList<PushDeliveryResult>> GetForUserAsync(string userId, CancellationToken ct);
    Task<IReadOnlyList<PushDeliveryResult>> GetRecentAsync(int limit, CancellationToken ct);
}

public sealed class InMemoryPushDeliveryTracker : IPushDeliveryTracker
{
    private readonly ConcurrentBag<PushDeliveryResult> _log = new();

    public Task RecordAsync(PushDeliveryResult result, CancellationToken ct)
    {
        _log.Add(result);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PushDeliveryResult>> GetForUserAsync(string userId, CancellationToken ct)
    {
        var results = _log.Where(r => r.UserId == userId).ToArray();
        return Task.FromResult<IReadOnlyList<PushDeliveryResult>>(results);
    }

    public Task<IReadOnlyList<PushDeliveryResult>> GetRecentAsync(int limit, CancellationToken ct)
    {
        var results = _log.Take(limit).ToArray();
        return Task.FromResult<IReadOnlyList<PushDeliveryResult>>(results);
    }

    public IReadOnlyList<PushDeliveryResult> All => _log.ToArray();
}
