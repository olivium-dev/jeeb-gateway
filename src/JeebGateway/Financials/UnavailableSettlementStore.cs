using JeebGateway.Infrastructure;

namespace JeebGateway.Financials;

/// <summary>
/// Fail-closed compatibility seam. Wallet-service owns settlement money, but
/// it does not yet expose the row-shaped query/mutation contract inherited by
/// legacy gateway controllers.
/// </summary>
public sealed class UnavailableSettlementStore : ISettlementStore
{
    public Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct) => Fail<(Settlement, bool)>();
    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct) => Fail<Settlement?>();
    public Task<IReadOnlyList<Settlement>> ListByJeeberAsync(string jeeberId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct, IReadOnlyCollection<string>? codStates = null) => Fail<IReadOnlyList<Settlement>>();
    public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct) => Fail<Settlement?>();
    public Task<bool> SetLedgerEntryAsync(string settlementId, string ledgerEntryId, CancellationToken ct) => Fail<bool>();
    public Task<IReadOnlyList<Settlement>> ListUnpostedLedgerAsync(int limit, CancellationToken ct) => Fail<IReadOnlyList<Settlement>>();
    public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, DateTimeOffset at, CancellationToken ct) => Fail<Settlement?>();
    public Task<bool> ReplacePendingAsync(string deliveryId, Settlement settled, CancellationToken ct) => Fail<bool>();
    public Task<IReadOnlyList<Settlement>> ListRecordedInWindowAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, int limit, CancellationToken ct) => Fail<IReadOnlyList<Settlement>>();
    public Task MarkBatchedAsync(IReadOnlyList<string> settlementIds, Guid batchId, DateTimeOffset at, CancellationToken ct) => Fail();
    public Task MarkPaidByBatchAsync(Guid batchId, DateTimeOffset paidAt, CancellationToken ct) => Fail();
    public Task<IReadOnlyList<Settlement>> ListPageForAdminAsync(AdminSettlementPortalFilter filter, int limit, CancellationToken ct) => Fail<IReadOnlyList<Settlement>>();

    private static OwnerCapabilityUnavailableException Error() =>
        new("wallet-service settlement projection");
    private static Task Fail() => Task.FromException(Error());
    private static Task<T> Fail<T>() => Task.FromException<T>(Error());
}
