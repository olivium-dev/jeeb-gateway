namespace JeebGateway.Financials;

// gwdbx W2-R02: the four settlement tables are dropped (migration 0052) and no replacement exists.
// UpstreamExceptionHandler maps this to a typed 503 so a caller sees "gone", never "empty" (O10).
public sealed class SettlementStoreRetiredException : InvalidOperationException
{
    public const string ProblemType = "https://jeeb.dev/errors/settlement-store-retired";

    public SettlementStoreRetiredException(string member)
        : base($"Settlement store retired (gwdbx W2-R02): '{member}' cannot be served — the gateway "
               + "settlement tables were dropped by migration 0052 and no replacement store exists yet.")
    {
        Member = member;
    }

    public string Member { get; }
}

// gwdbx W2-R02: no-op ISettlementStore (NullJeebWalletLedgerReader shape). Writes and caller-facing
// reads fault; the three background-sweep reads return empty because zero rows really do exist.
public sealed class NullSettlementStore : ISettlementStore
{
    public Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(TryInsertAsync));

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(GetByDeliveryAsync));

    public Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct,
        IReadOnlyCollection<string>? codStates = null)
        => throw new SettlementStoreRetiredException(nameof(ListByJeeberAsync));

    public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(GetByIdAsync));

    public Task<bool> SetLedgerEntryAsync(string settlementId, string ledgerEntryId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(SetLedgerEntryAsync));

    // Sweep read (SettlementLedgerReconciler, every 60 s): zero rows exist, so zero need replaying.
    public Task<IReadOnlyList<Settlement>> ListUnpostedLedgerAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, DateTimeOffset at, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(MarkReceiptGeneratedAsync));

    public Task<bool> ReplacePendingAsync(string deliveryId, Settlement settled, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(ReplacePendingAsync));

    // Sweep read (WeeklySettlementBatch cron): nothing recorded, so nothing to batch.
    public Task<IReadOnlyList<Settlement>> ListRecordedInWindowAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        int limit,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task MarkBatchedAsync(
        IReadOnlyList<string> settlementIds,
        Guid batchId,
        DateTimeOffset at,
        CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(MarkBatchedAsync));

    public Task MarkPaidByBatchAsync(Guid batchId, DateTimeOffset paidAt, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(MarkPaidByBatchAsync));

    // Overrides the interface's default empty page: the admin portal is exactly the
    // "confident empty settlement screen" an operator must never be shown.
    public Task<IReadOnlyList<Settlement>> ListPageForAdminAsync(
        AdminSettlementPortalFilter filter, int limit, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(ListPageForAdminAsync));

    // Overrides the interface's default 0m for the same reason: a zero is a claim, not an absence.
    public Task<decimal> SumEarningsAsync(IReadOnlyCollection<string>? codStates, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(SumEarningsAsync));

    // Sweep read (CodWalletMirrorReconciler, W2-05): nothing settled, so nothing to mirror.
    public Task<IReadOnlyList<Settlement>> ListWalletUnmirroredAsync(
        DateTimeOffset from, int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task<bool> SetWalletTxIdAsync(string settlementId, string walletTxId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(SetWalletTxIdAsync));
}

// gwdbx W2-R02: no-op ISettlementBatchStore. Admin batch reads and mark-paid fault; only the weekly
// cron's sweep read returns empty so the job logs "no unsettled items" instead of erroring weekly.
public sealed class NullSettlementBatchStore : ISettlementBatchStore
{
    public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

    public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(MarkBatchProcessedAsync));

    public Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(CreateOrGetBatchAsync));

    public Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(GetByIdAsync));

    public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(ListByStatusAsync));

    public Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(MarkPaidAsync));
}

// gwdbx W2-R02: no-op ISettlementEnqueueStore. Both members fault rather than answer a money
// idempotency question ("already enqueued?") the dropped table can no longer answer truthfully.
public sealed class NullSettlementEnqueueStore : ISettlementEnqueueStore
{
    public Task<bool> TryEnqueueAsync(string deliveryId, DateTimeOffset at, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(TryEnqueueAsync));

    public Task<bool> IsEnqueuedAsync(string deliveryId, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(IsEnqueuedAsync));
}

// gwdbx W2-R02: no-op ISettlementLedgerClient. Faults rather than mint a ledger id without the
// durable idempotency memo — inventing one is the double-credit bug migration 0044 closed.
public sealed class NullSettlementLedgerClient : ISettlementLedgerClient
{
    public Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
        => throw new SettlementStoreRetiredException(nameof(PostLedgerEntryAsync));
}
