using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// M1 (P0 money-loss) + M2 (P1 re-batch) — sprint-009 money-resilience-audit.
///
/// The weekly payout batch selects settlements via
/// <see cref="ISettlementStore.ListRecordedInWindowAsync"/>. Handover writes a
/// placeholder row (state=pending_settlement, cod_state=recorded, goods_cost=0 →
/// min-fee 1000 LBP commission). Before the fix the batch selected purely on
/// cod_state='recorded', sweeping the placeholder in as a phantom -1000 LBP net
/// (underpaying the Jeeber and booking uncollected commission).
///
/// These tests pin the guard: unsettled placeholders are EXCLUDED from the batch
/// window; truly-settled rows (state=settled AND state=receipt_generated) are
/// INCLUDED; and a placeholder replaced by a real settlement appears exactly once.
/// Exercised on the in-memory store — the Postgres store carries the identical
/// `state &lt;&gt; 'pending_settlement'` guard.
/// </summary>
public class SettlementBatchWindowGuardTests
{
    private readonly InMemorySettlementStore _store = new();

    private static readonly DateTimeOffset WindowStart = new(2026, 7, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd   = new(2026, 7, 8, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset InWindow    = new(2026, 7, 4, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ListRecordedInWindow_ExcludesUnsettledPlaceholder_And_IncludesSettledRow()
    {
        // Placeholder written at handover: goods_cost=0, min-fee commission=1000,
        // state=pending_settlement, cod_state=recorded.
        var placeholder = MakePlaceholder("del-pending", "jeeber-1");
        placeholder.Commission.Should().Be(1_000m, "zero goods-cost floors to the 1000 LBP min fee");

        var settled = MakeSettled("del-settled", "jeeber-1", goodsCost: 100_000m);

        await _store.TryInsertAsync(placeholder, CancellationToken.None);
        await _store.TryInsertAsync(settled, CancellationToken.None);

        var batch = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);

        batch.Select(s => s.DeliveryId).Should().ContainSingle()
            .Which.Should().Be("del-settled", "only the truly-settled row may enter the payout batch");
        batch.Should().NotContain(s => s.DeliveryId == "del-pending",
            "the unsettled handover placeholder must never be swept into the weekly payout");
    }

    [Fact]
    public async Task ListRecordedInWindow_IncludesReceiptGeneratedRow_NoRegression()
    {
        // A settled delivery whose receipt was already read advances to
        // receipt_generated while cod_state stays 'recorded'. It is still owed a payout,
        // so pinning the guard to state='settled' would have wrongly dropped it.
        var receiptGenerated = MakeSettled("del-receipt", "jeeber-2");
        receiptGenerated.State = SettlementState.ReceiptGenerated;

        await _store.TryInsertAsync(receiptGenerated, CancellationToken.None);

        var batch = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);

        batch.Should().ContainSingle(s => s.DeliveryId == "del-receipt",
            "a settled-then-receipt-viewed delivery must still be paid out");
    }

    [Fact]
    public async Task ReplacePending_ThenBatch_IncludesRealSettlementExactlyOnce()
    {
        // M2: placeholder → jeeber settles (ReplacePendingAsync) → the real settlement
        // must appear in the batch exactly once (never the phantom placeholder).
        var placeholder = MakePlaceholder("del-replace", "jeeber-3");
        await _store.TryInsertAsync(placeholder, CancellationToken.None);

        // Before settle the delivery is invisible to the batch.
        var before = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);
        before.Should().NotContain(s => s.DeliveryId == "del-replace");

        var real = MakeSettled("del-replace", "jeeber-3", goodsCost: 250_000m);
        var replaced = await _store.ReplacePendingAsync("del-replace", real, CancellationToken.None);
        replaced.Should().BeTrue("the pending placeholder must be replaceable by the real settlement");

        var after = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);
        after.Where(s => s.DeliveryId == "del-replace").Should().ContainSingle()
            .Which.GoodsCost.Should().Be(250_000m, "the real settled amount, not the phantom placeholder");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Settlement MakePlaceholder(string deliveryId, string jeeberId)
    {
        var breakdown = CommissionCalculator.Calculate(0m, CommissionTier.Standard);
        return new Settlement
        {
            Id = Guid.NewGuid().ToString(),
            DeliveryId = deliveryId,
            JeeberId = jeeberId,
            ClientId = "client-1",
            TierId = "same-day",
            GoodsCost = breakdown.GoodsCost,
            CommissionTier = CommissionTier.Standard,
            CommissionRate = breakdown.CommissionRate,
            Commission = breakdown.Commission,
            Insurance = breakdown.Insurance,
            Total = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency = "LBP",
            PaymentMethod = "cash",
            State = SettlementState.PendingSettlement,
            CodState = CodSettlementState.Recorded,
            SettledAt = InWindow,
        };
    }

    private static Settlement MakeSettled(string deliveryId, string jeeberId, decimal goodsCost = 100_000m)
    {
        var breakdown = CommissionCalculator.Calculate(goodsCost, CommissionTier.Standard);
        return new Settlement
        {
            Id = Guid.NewGuid().ToString(),
            DeliveryId = deliveryId,
            JeeberId = jeeberId,
            ClientId = "client-1",
            TierId = "same-day",
            GoodsCost = breakdown.GoodsCost,
            CommissionTier = CommissionTier.Standard,
            CommissionRate = breakdown.CommissionRate,
            Commission = breakdown.Commission,
            Insurance = breakdown.Insurance,
            Total = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency = "LBP",
            PaymentMethod = "cash",
            State = SettlementState.Settled,
            CodState = CodSettlementState.Recorded,
            SettledAt = InWindow,
        };
    }
}
