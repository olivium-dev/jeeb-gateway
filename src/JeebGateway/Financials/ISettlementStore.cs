namespace JeebGateway.Financials;

/// <summary>
/// Persistence seam for Jeeb settlement rows. The MVP implementation
/// (<see cref="InMemorySettlementStore"/>) keeps the same shape as every
/// other in-memory store under the gateway: one ConcurrentDictionary,
/// short critical sections, and Task-returning APIs so the swap to
/// Postgres (paired with wallet-service / unified_payment_gateway
/// migrations) lands without changing call sites.
///
/// One settlement per delivery — the store rejects a second insert for
/// the same <see cref="Settlement.DeliveryId"/> so retries cannot
/// double-post ledger entries.
/// </summary>
public interface ISettlementStore
{
    /// <summary>
    /// Inserts a new settlement. Returns the existing row when a settlement
    /// for the same <see cref="Settlement.DeliveryId"/> already exists —
    /// the second return value tells the caller whether the insert was new.
    /// </summary>
    Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct);

    /// <summary>Looks up a settlement by its delivery id.</summary>
    Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct);

    /// <summary>Looks up a settlement by its own primary key.</summary>
    Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct);

    /// <summary>
    /// Stamps <see cref="Settlement.LedgerEntryId"/> after wallet-service
    /// has accepted the ledger post. Returns false when the settlement is
    /// unknown.
    /// </summary>
    Task<bool> SetLedgerEntryAsync(string settlementId, string ledgerEntryId, CancellationToken ct);

    /// <summary>
    /// Flips the row from <see cref="SettlementState.Settled"/> to
    /// <see cref="SettlementState.ReceiptGenerated"/> on first receipt
    /// read. Idempotent — repeat calls do not advance the timestamp.
    /// </summary>
    Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, DateTimeOffset at, CancellationToken ct);
}
