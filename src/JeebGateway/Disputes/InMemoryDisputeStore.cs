using System.Collections.Concurrent;

namespace JeebGateway.Disputes;

/// <summary>
/// MVP in-memory implementation of <see cref="IDisputeStore"/>. Mirrors
/// the row shape the Postgres implementation will eventually own.
/// </summary>
public class InMemoryDisputeStore : IDisputeStore
{
    private readonly ConcurrentDictionary<string, Dispute> _byId = new();
    private readonly object _gate = new();

    public Task<Dispute> AddAsync(Dispute dispute, CancellationToken ct)
    {
        _byId[dispute.Id] = dispute;
        return Task.FromResult(dispute);
    }

    public Task<Dispute?> GetByIdAsync(string disputeId, CancellationToken ct)
    {
        _byId.TryGetValue(disputeId, out var d);
        return Task.FromResult(d);
    }

    public Task<IReadOnlyList<Dispute>> ListForUserAsync(string userId, CancellationToken ct)
    {
        var items = _byId.Values
            .Where(d => string.Equals(d.FiledByUserId, userId, StringComparison.Ordinal))
            .OrderByDescending(d => d.FiledAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<Dispute>>(items);
    }

    public Task<Dispute?> GetOpenForDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        var open = _byId.Values
            .Where(d => string.Equals(d.DeliveryId, deliveryId, StringComparison.Ordinal)
                        && !DisputeState.IsTerminal(d.State))
            .OrderByDescending(d => d.FiledAt)
            .FirstOrDefault();
        return Task.FromResult(open);
    }

    public Task<Dispute?> UpdateStateAsync(string disputeId, DisputeStatePatch patch, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(disputeId, out var existing))
            {
                return Task.FromResult<Dispute?>(null);
            }

            existing.State = patch.State;
            existing.ReviewedAt = patch.ReviewedAt;
            existing.ResolverAdminId = patch.ResolverAdminId;
            existing.Resolution = patch.Resolution;
            return Task.FromResult<Dispute?>(existing);
        }
    }
}
