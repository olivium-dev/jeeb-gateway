using System.Collections.Concurrent;

namespace JeebGateway.Financials;

/// <summary>
/// JEB-1495: lightweight "pending-settlement" intent store. Records which
/// delivery IDs have had a settlement enqueued (i.e. have reached the
/// handover-complete state) so the intent survives in-process and can be
/// read by <c>GET /v1/deliveries/{id}/settlement</c> without needing to
/// re-resolve the delivery status on every read.
///
/// One entry per delivery — <see cref="TryEnqueueAsync"/> is idempotent:
/// a duplicate call for the same <paramref name="deliveryId"/> is a no-op
/// and the original enqueue timestamp is preserved.
/// </summary>
public interface ISettlementEnqueueStore
{
    /// <summary>
    /// Idempotently marks <paramref name="deliveryId"/> as "settlement
    /// pending". Returns true on the first call, false on every subsequent
    /// call for the same id.
    /// </summary>
    Task<bool> TryEnqueueAsync(string deliveryId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Returns true when a settlement intent has been enqueued for
    /// <paramref name="deliveryId"/>.
    /// </summary>
    Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct);
}

/// <summary>
/// In-memory <see cref="ISettlementEnqueueStore"/> for MVP. A ConcurrentDictionary
/// keyed on deliveryId is sufficient for single-node deployments; the durable
/// upgrade path (backed by jeeb-state-service R1 idempotency KV) is wired in a
/// future PR once the settlement-service Postgres migration is ready.
/// </summary>
public sealed class InMemorySettlementEnqueueStore : ISettlementEnqueueStore
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _enqueued = new(StringComparer.Ordinal);

    public Task<bool> TryEnqueueAsync(string deliveryId, DateTimeOffset at, CancellationToken ct)
        => Task.FromResult(_enqueued.TryAdd(deliveryId, at));

    public Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct)
        => Task.FromResult(_enqueued.ContainsKey(deliveryId));
}
