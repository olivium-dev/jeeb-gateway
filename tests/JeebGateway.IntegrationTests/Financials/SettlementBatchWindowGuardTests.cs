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
/// zero USD commission). Before the fix the batch selected purely on
/// cod_state='recorded', sweeping the placeholder in as a phantom zero-gross row
/// (underpaying the Jeeber and booking uncollected commission).
///
/// These tests pin the guard: unsettled placeholders are EXCLUDED from the batch
/// window; truly-settled rows (state=settled AND state=receipt_generated) are
/// INCLUDED; and a placeholder replaced by a real settlement appears exactly once.
/// They also pin the RECEIPT-BEFORE-SETTLE escape hatch closure: a placeholder read
/// via <see cref="ISettlementStore.MarkReceiptGeneratedAsync"/> must NOT advance to
/// receipt_generated (only a settled row may), so it can never leak into the batch.
/// Exercised on the in-memory store — the Postgres store carries the identical
/// positive `state IN ('settled','receipt_generated')` batch guard and the identical
/// `state = 'settled'` receipt-transition guard.
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
        // Placeholder written at handover: goods_cost=0, commission=0,
        // state=pending_settlement, cod_state=recorded.
        var placeholder = MakePlaceholder("del-pending", "jeeber-1");
        placeholder.Commission.Should().Be(0m, "zero goods-cost has no minimum commission floor");

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

    [Fact]
    public async Task ReceiptRead_OnPendingPlaceholder_DoesNotAdvance_And_StaysOutOfBatch()
    {
        // ESCAPE HATCH (Codex gw-leg12-b2 P0): a party reads the receipt BEFORE the Jeeber
        // settles. MarkReceiptGeneratedAsync must NOT flip the pending_settlement placeholder
        // to receipt_generated — otherwise it satisfies the batch's state guard and underpays
        // the Jeeber (phantom min-fee net, no ledger entry).
        var placeholder = MakePlaceholder("del-early-receipt", "jeeber-4");
        await _store.TryInsertAsync(placeholder, CancellationToken.None);

        var stamped = await _store.MarkReceiptGeneratedAsync(placeholder.Id, InWindow, CancellationToken.None);

        stamped!.State.Should().Be(SettlementState.PendingSettlement,
            "a pre-settle receipt read must NOT advance a placeholder out of pending_settlement");
        stamped.ReceiptGeneratedAt.Should().BeNull("no receipt timestamp is stamped on an unsettled placeholder");

        var batch = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);
        batch.Should().NotContain(s => s.DeliveryId == "del-early-receipt",
            "a placeholder that was 'receipt-read' before settle must still be EXCLUDED from the payout batch");
    }

    [Fact]
    public async Task ReceiptRead_AfterRealSettle_Advances_And_IsIncludedInBatch()
    {
        // The legitimate path: placeholder → real settle (state=settled) → receipt read
        // (state=receipt_generated). The settled-then-viewed row MUST be paid out.
        var placeholder = MakePlaceholder("del-settle-then-receipt", "jeeber-5");
        await _store.TryInsertAsync(placeholder, CancellationToken.None);

        var real = MakeSettled("del-settle-then-receipt", "jeeber-5", goodsCost: 80_000m);
        (await _store.ReplacePendingAsync("del-settle-then-receipt", real, CancellationToken.None))
            .Should().BeTrue();

        var stamped = await _store.MarkReceiptGeneratedAsync(real.Id, InWindow, CancellationToken.None);
        stamped!.State.Should().Be(SettlementState.ReceiptGenerated,
            "a settled row advances to receipt_generated on first receipt read");

        var batch = await _store.ListRecordedInWindowAsync(WindowStart, WindowEnd, 100, CancellationToken.None);
        batch.Where(s => s.DeliveryId == "del-settle-then-receipt").Should().ContainSingle()
            .Which.GoodsCost.Should().Be(80_000m, "the real settled-then-viewed row is paid out exactly once");
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
            Currency = "USD",
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
            Currency = "USD",
            PaymentMethod = "cash",
            State = SettlementState.Settled,
            CodState = CodSettlementState.Recorded,
            SettledAt = InWindow,
        };
    }
}
