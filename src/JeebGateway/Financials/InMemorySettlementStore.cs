using System.Collections.Concurrent;

namespace JeebGateway.Financials;

/// <summary>
/// MVP in-memory settlement store. Two parallel maps so both the primary
/// (settlement id) and secondary (delivery id) lookups stay O(1); inserts
/// take a short write-lock so the uniqueness check and the dual insert
/// form a single atomic block.
///
/// Production swap (T-backend-bff-wallet) will move persistence behind
/// wallet-service via the NSwag-generated client.
/// </summary>
public sealed class InMemorySettlementStore : ISettlementStore
{
    private readonly ConcurrentDictionary<string, Settlement> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _deliveryIndex = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    public Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (_deliveryIndex.TryGetValue(settlement.DeliveryId, out var existingId)
                && _byId.TryGetValue(existingId, out var existing))
            {
                return Task.FromResult((Clone(existing), false));
            }

            _byId[settlement.Id] = settlement;
            _deliveryIndex[settlement.DeliveryId] = settlement.Id;
            return Task.FromResult((Clone(settlement), true));
        }
    }

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (_deliveryIndex.TryGetValue(deliveryId, out var id) && _byId.TryGetValue(id, out var s))
        {
            return Task.FromResult<Settlement?>(Clone(s));
        }
        return Task.FromResult<Settlement?>(null);
    }

    public Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
    {
        return Task.FromResult(_byId.TryGetValue(settlementId, out var s) ? Clone(s) : null);
    }

    public Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
        string jeeberId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct,
        IReadOnlyCollection<string>? codStates = null)
    {
        // Snapshot the values up front (ConcurrentDictionary enumeration is safe
        // but we clone to keep the row immutable for the caller).
        var rows = _byId.Values
            .Where(s => string.Equals(s.JeeberId, jeeberId, StringComparison.Ordinal))
            .Where(s => (from is null || s.SettledAt >= from.Value)
                     && (to is null || s.SettledAt <= to.Value))
            .Where(s => codStates is null || codStates.Count == 0 || codStates.Contains(s.CodState))
            .OrderBy(s => s.SettledAt)
            .Select(Clone)
            .ToList();
        return Task.FromResult<IReadOnlyList<Settlement>>(rows);
    }

    public Task<bool> SetLedgerEntryAsync(string settlementId, string ledgerEntryId, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(settlementId, out var row))
            {
                return Task.FromResult(false);
            }
            row.LedgerEntryId = ledgerEntryId;
            return Task.FromResult(true);
        }
    }

    public Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(settlementId, out var row))
            {
                return Task.FromResult<Settlement?>(null);
            }

            if (row.State != SettlementState.ReceiptGenerated)
            {
                row.State = SettlementState.ReceiptGenerated;
                row.ReceiptGeneratedAt = at;
            }
            return Task.FromResult<Settlement?>(Clone(row));
        }
    }

    public Task<bool> ReplacePendingAsync(string deliveryId, Settlement settled, CancellationToken ct)
    {
        lock (_writeLock)
        {
            if (!_deliveryIndex.TryGetValue(deliveryId, out var existingId)
                || !_byId.TryGetValue(existingId, out var existing))
            {
                return Task.FromResult(false);
            }

            if (!string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
            {
                return Task.FromResult(false);
            }

            // Remove the old placeholder keyed under its old id, insert the real row.
            _byId.TryRemove(existingId, out _);
            _byId[settled.Id] = settled;
            _deliveryIndex[deliveryId] = settled.Id;
            return Task.FromResult(true);
        }
    }

    private static Settlement Clone(Settlement s) => new()
    {
        Id = s.Id,
        DeliveryId = s.DeliveryId,
        ClientId = s.ClientId,
        JeeberId = s.JeeberId,
        TierId = s.TierId,
        GoodsCost = s.GoodsCost,
        CommissionTier = s.CommissionTier,
        CommissionRate = s.CommissionRate,
        Commission = s.Commission,
        Insurance = s.Insurance,
        Total = s.Total,
        MinimumFeeApplied = s.MinimumFeeApplied,
        Currency = s.Currency,
        PaymentMethod = s.PaymentMethod,
        State = s.State,
        SettledAt = s.SettledAt,
        ReceiptGeneratedAt = s.ReceiptGeneratedAt,
        LedgerEntryId = s.LedgerEntryId,
        BatchId = s.BatchId,
        BatchedAt = s.BatchedAt,
        PaidAt = s.PaidAt,
        CodState = s.CodState,
    };
}
