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

    /// <summary>
    /// Returns every settlement recorded for <paramref name="jeeberId"/> whose
    /// <see cref="Settlement.SettledAt"/> falls in the inclusive
    /// <paramref name="from"/>..<paramref name="to"/> window, ordered oldest
    /// first. Feeds the earnings aggregation (T-backend-018): the gateway sums
    /// the verbatim per-settlement gross/commission/net — zero re-arithmetic on
    /// the wallet copy (BR-16). A null window bound means unbounded on that side
    /// (lifetime read).
    ///
    /// <para>JEB-58: pass <paramref name="codStates"/> to restrict by COD lifecycle state.
    /// Null/empty = no cod_state filter (returns all). Earnings endpoints pass
    /// <c>["batched","paid"]</c> — <c>recorded</c> rows are pending batch and excluded
    /// from earnings per TL-PIN-JEB-510 §3.</para>
    /// </summary>
    Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct,
        IReadOnlyCollection<string>? codStates = null);

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

    /// <summary>
    /// FT-07: atomically replaces an existing <see cref="SettlementState.PendingSettlement"/>
    /// placeholder row with the supplied fully-computed <paramref name="settled"/> row. Returns
    /// <c>true</c> when the pending row was found and replaced; <c>false</c> when no pending row
    /// exists for the delivery (the caller should fall through to
    /// <see cref="TryInsertAsync"/>). Idempotent on <see cref="Settlement.DeliveryId"/> so a
    /// retry of the replace never double-posts the ledger entry.
    /// </summary>
    Task<bool> ReplacePendingAsync(string deliveryId, Settlement settled, CancellationToken ct);
}
