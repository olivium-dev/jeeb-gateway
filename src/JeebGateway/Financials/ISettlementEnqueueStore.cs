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
