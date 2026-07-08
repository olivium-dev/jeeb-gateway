using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// QA-PRE-JEB-488: COD Settlement idempotency, commission math, and state
/// machine tests (JEB-56). These tests exercise the in-memory store (fast,
/// no external Postgres required); PostgresSettlementStore integration tests
/// require Testcontainers and run in the CI integration suite.
/// </summary>
public class SettlementIdempotencyTests
{
    private readonly InMemorySettlementStore _store = new();
    private readonly TimeProvider _clock = TimeProvider.System;

    // ── P1: Idempotency — repeat insert for same deliveryId → one row ─────────

    [Fact]
    public async Task TryInsertAsync_SameDeliveryId_ReturnsExistingRow_OnSecondCall()
    {
        var settlement = MakeSettlement("del-001", "jeeber-001");

        var (row1, inserted1) = await _store.TryInsertAsync(settlement, CancellationToken.None);
        var (row2, inserted2) = await _store.TryInsertAsync(
            MakeSettlement("del-001", "jeeber-001"), CancellationToken.None);

        inserted1.Should().BeTrue("first insert should succeed");
        inserted2.Should().BeFalse("second insert for same deliveryId must be deduplicated");
        row1.Id.Should().Be(row2.Id, "both calls must return the same settlement row");
    }

    [Fact]
    public async Task TryInsertAsync_SameDeliveryId_NumbersUnchangedOnSecondCall()
    {
        var settlement = MakeSettlement("del-002", "jeeber-001", goodsCost: 150_000m);
        await _store.TryInsertAsync(settlement, CancellationToken.None);

        // Second call has different goodsCost — should be ignored.
        var differentAmountSettlement = MakeSettlement("del-002", "jeeber-001", goodsCost: 200_000m);
        var (row2, inserted2) = await _store.TryInsertAsync(differentAmountSettlement, CancellationToken.None);

        inserted2.Should().BeFalse();
        row2.GoodsCost.Should().Be(150_000m, "original goodsCost must not be overwritten on retry");
    }

    // ── P2: Commission math ───────────────────────────────────────────────────

    [Theory]
    [InlineData("urgent",      150_000, 0.10, 15_000, 0, 15_000)]   // Express flat 10%
    [InlineData("same-day",    150_000, 0.10, 15_000, 0, 15_000)]   // Standard flat 10%
    [InlineData("economy",     150_000, 0.10, 15_000, 0, 15_000)]   // Standard flat 10%
    [InlineData("on-the-way",  150_000, 0.10, 15_000, 0, 15_000)]   // OnTheWay flat 10%
    [InlineData("unknown",     150_000, 0.10, 15_000, 0, 15_000)]   // Fallback -> Standard
    [InlineData("scheduled",     5_000, 0.10,    500, 0,    500)]   // No floor
    public void CommissionCalculator_MatchesPolicy(
        string tierId, decimal goodsCost,
        decimal expectedRate, decimal expectedCommission,
        decimal expectedInsurance, decimal expectedTotal)
    {
        var tier = CommissionCalculator.ResolveTier(tierId);
        var result = CommissionCalculator.Calculate(goodsCost, tier);

        result.CommissionRate.Should().Be(expectedRate, "rate must match tier policy");
        result.Commission.Should().Be(expectedCommission, "commission must be exact decimal");
        result.Insurance.Should().Be(expectedInsurance, "insurance is not applied");
        result.Total.Should().Be(expectedTotal, "total must equal commission only");
    }

    [Fact]
    public void CommissionCalculator_NoFloatArithmetic_NoMinimumFeeCase()
    {
        // goodsCost=6,666, Standard: 6666 * 0.10 = 666.60, no floor applied.
        var result = CommissionCalculator.Calculate(6_666m, CommissionTier.Standard);

        result.MinimumFeeApplied.Should().BeFalse("there is no minimum commission floor");
        result.Commission.Should().Be(666.60m, "commission is exactly 10% of the accepted offer amount");
        // Verify no floating-point drift: decimal arithmetic only.
        result.Commission.GetType().Should().Be(typeof(decimal));
        result.Total.Should().Be(result.Commission);
    }

    // ── P3: State machine — cod_state transitions ─────────────────────────────

    [Fact]
    public async Task CodState_StartsAtRecorded_AfterInsert()
    {
        var settlement = MakeSettlement("del-003", "jeeber-002", codState: CodSettlementState.Recorded);
        var (row, _) = await _store.TryInsertAsync(settlement, CancellationToken.None);

        row.CodState.Should().Be(CodSettlementState.Recorded);
    }

    [Fact]
    public async Task MarkBatchedAsync_TransitionsRecordedToBatched()
    {
        var settlement = MakeSettlement("del-004", "jeeber-003");
        var (row, _) = await _store.TryInsertAsync(settlement, CancellationToken.None);

        var batchId = Guid.NewGuid();
        await _store.MarkBatchedAsync(new[] { row.Id }, batchId, DateTimeOffset.UtcNow, CancellationToken.None);

        var updated = await _store.GetByDeliveryAsync("del-004", CancellationToken.None);
        updated!.CodState.Should().Be(CodSettlementState.Batched, "cron transitions to batched");
        updated.BatchId.Should().Be(batchId);
    }

    [Fact]
    public async Task MarkBatchedAsync_DoesNotTransition_AlreadyBatched()
    {
        var settlement = MakeSettlement("del-005", "jeeber-004");
        var (row, _) = await _store.TryInsertAsync(settlement, CancellationToken.None);

        var batchId1 = Guid.NewGuid();
        var batchId2 = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;

        await _store.MarkBatchedAsync(new[] { row.Id }, batchId1, at, CancellationToken.None);
        await _store.MarkBatchedAsync(new[] { row.Id }, batchId2, at, CancellationToken.None); // second batch run

        var updated = await _store.GetByDeliveryAsync("del-005", CancellationToken.None);
        updated!.BatchId.Should().Be(batchId1, "batched→batched transition is a no-op; original batch preserved");
    }

    [Fact]
    public async Task MarkPaidByBatchAsync_TransitionsBatchedToPaid()
    {
        var settlement = MakeSettlement("del-006", "jeeber-005");
        var (row, _) = await _store.TryInsertAsync(settlement, CancellationToken.None);

        var batchId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await _store.MarkBatchedAsync(new[] { row.Id }, batchId, at, CancellationToken.None);
        await _store.MarkPaidByBatchAsync(batchId, at.AddDays(7), CancellationToken.None);

        var updated = await _store.GetByDeliveryAsync("del-006", CancellationToken.None);
        updated!.CodState.Should().Be(CodSettlementState.Paid, "admin mark-paid transitions to paid");
        updated.PaidAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecordedCannotTransitionDirectlyToPaid_WithoutBatch()
    {
        var settlement = MakeSettlement("del-007", "jeeber-006");
        var (row, _) = await _store.TryInsertAsync(settlement, CancellationToken.None);

        // Trying to mark paid by a batch that this row has no link to.
        await _store.MarkPaidByBatchAsync(Guid.NewGuid(), DateTimeOffset.UtcNow, CancellationToken.None);

        var updated = await _store.GetByDeliveryAsync("del-007", CancellationToken.None);
        updated!.CodState.Should().Be(CodSettlementState.Recorded,
            "recorded→paid without batching is forbidden; state must remain recorded");
    }

    // ── P5: No float arithmetic — decimal types only ─────────────────────────

    [Theory]
    [InlineData(12_345.67)]
    [InlineData(99_999.99)]
    [InlineData(0.01)]
    [InlineData(1_000_000)]
    public void AllCommissionFields_AreDecimal(double goodsCostDouble)
    {
        // Convert from test input to decimal (tests cannot use decimal literals in [InlineData])
        var goodsCost = (decimal)goodsCostDouble;
        var result = CommissionCalculator.Calculate(goodsCost, CommissionTier.Standard);

        // All results must be decimal — verify no lossy float intermediary.
        result.GoodsCost.GetType().Should().Be(typeof(decimal));
        result.Commission.GetType().Should().Be(typeof(decimal));
        result.Insurance.GetType().Should().Be(typeof(decimal));
        result.Total.GetType().Should().Be(typeof(decimal));

        // New money model (Q-001): flat 10% commission, no insurance, no floor — Total == Commission only.
        result.Insurance.Should().Be(0m, "insurance surcharge is retired under Q-001");
        result.Total.Should().Be(result.Commission, "total equals commission only — goods cost and insurance never accumulate into it");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Settlement MakeSettlement(
        string deliveryId,
        string jeeberId,
        decimal goodsCost = 100_000m,
        string? codState = null,
        string? state = null,
        DateTimeOffset? settledAt = null)
    {
        var tier = CommissionTier.Standard;
        var breakdown = CommissionCalculator.Calculate(goodsCost, tier);
        return new Settlement
        {
            Id              = Guid.NewGuid().ToString(),
            DeliveryId      = deliveryId,
            JeeberId        = jeeberId,
            ClientId        = "client-001",
            TierId          = "same-day",
            GoodsCost       = breakdown.GoodsCost,
            CommissionTier  = tier,
            CommissionRate  = breakdown.CommissionRate,
            Commission      = breakdown.Commission,
            Insurance       = breakdown.Insurance,
            Total           = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency        = "USD",
            PaymentMethod   = "cash",
            State           = state ?? SettlementState.Settled,
            CodState        = codState ?? CodSettlementState.Recorded,
            SettledAt       = settledAt ?? DateTimeOffset.UtcNow,
        };
    }
}
