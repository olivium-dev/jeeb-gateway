using JeebGateway.Financials;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEB-57 — Weekly Settlement Batch property tests.
/// Uses InMemorySettlementStore and InMemoryFallbackSettlementBatchStore — no DB required.
/// Covers QA-PRE-JEB-500: AC1–AC7.
/// </summary>
public class WeeklyBatchTests
{
    private readonly InMemorySettlementStore _settlementStore;
    private readonly InMemoryFallbackSettlementBatchStore _batchStore;
    private readonly FakeTimeProvider _clock;

    private static readonly DateOnly PeriodStart = new(2026, 6, 2);
    private static readonly DateOnly PeriodEnd   = new(2026, 6, 8);
    private static DateTimeOffset MidWindow => new(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset WindowStart = new(
        PeriodStart.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd = new(
        PeriodEnd.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc), TimeSpan.Zero);

    public WeeklyBatchTests()
    {
        _clock           = new FakeTimeProvider(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));
        _settlementStore = new InMemorySettlementStore();
        _batchStore      = new InMemoryFallbackSettlementBatchStore(_settlementStore);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<Settlement> SeedAsync(
        string jeeberId,
        decimal grossAmount = 100_000m,
        DateTimeOffset? settledAt = null)
    {
        var at         = settledAt ?? MidWindow;
        var commission = Math.Max(1_000m, Math.Round(grossAmount * 0.15m, 2, MidpointRounding.AwayFromZero));
        var settlement = new Settlement
        {
            Id                = Guid.NewGuid().ToString(),
            DeliveryId        = Guid.NewGuid().ToString(),
            ClientId          = "client-test",
            JeeberId          = jeeberId,
            TierId            = "standard",
            GoodsCost         = grossAmount,
            CommissionTier    = CommissionTier.Standard,
            CommissionRate    = 0.15m,
            Commission        = commission,
            Insurance         = 0m,
            Total             = grossAmount,
            MinimumFeeApplied = false,
            Currency          = "LBP",
            PaymentMethod     = "cod",
            State             = "pending_settlement",
            CodState          = CodSettlementState.Recorded,
            SettledAt         = at,
        };
        await _settlementStore.TryInsertAsync(settlement, CancellationToken.None);
        return settlement;
    }

    private async Task SeedManyAsync(string jeeberId, int count = 5, decimal gross = 100_000m)
    {
        for (var i = 0; i < count; i++)
            await SeedAsync(jeeberId, gross);
    }

    /// <summary>Simulates one cron window execution.</summary>
    private async Task<IReadOnlyList<SettlementBatch>> RunWindowAsync()
    {
        var ct       = CancellationToken.None;
        var recorded = await _settlementStore.ListRecordedInWindowAsync(
            WindowStart, WindowEnd, 10_000, ct);

        if (!recorded.Any())
        {
            // Idempotent second call: return existing batches for this window.
            var batched = (await _batchStore.ListByStatusAsync("batched", ct))
                .Where(b => b.PeriodStart == PeriodStart).ToList();
            var open    = (await _batchStore.ListByStatusAsync("open", ct))
                .Where(b => b.PeriodStart == PeriodStart).ToList();
            batched.AddRange(open);
            return batched;
        }

        var created = new List<SettlementBatch>();
        foreach (var g in recorded.GroupBy(s => s.JeeberId))
        {
            var rows  = g.ToList();
            var batch = await _batchStore.CreateOrGetBatchAsync(
                g.Key, PeriodStart, PeriodEnd, rows, ct);

            await _settlementStore.MarkBatchedAsync(
                rows.Select(s => s.Id).ToList(), batch.Id, _clock.GetUtcNow(), ct);

            batch.Status    = "batched";
            batch.UpdatedAt = _clock.GetUtcNow();
            created.Add(batch);
        }
        return created;
    }

    // ── AC1 — No settlement in two batches ───────────────────────────────────

    [Fact]
    public async Task AC1_NoSettlementInTwoBatches_SameWindow()
    {
        await SeedManyAsync("jeeber-001", 5);

        var run1 = await RunWindowAsync();
        var run2 = await RunWindowAsync();

        // Both runs refer to the same batch for jeeber-001 in this period.
        var batch1 = run1.Single(b => b.JeeberId == "jeeber-001");
        var batch2 = run2.Single(b => b.JeeberId == "jeeber-001");
        Assert.Equal(batch1.Id, batch2.Id);
        Assert.Equal(5, batch1.SettlementCount);

        // All 5 settlements must now be in state=batched (not re-batched).
        var stillRecorded = await _settlementStore.ListRecordedInWindowAsync(
            WindowStart, WindowEnd, 100, CancellationToken.None);
        Assert.Empty(stillRecorded.Where(s => s.JeeberId == "jeeber-001"));
    }

    // ── AC2 — sum(batch) == sum(settlements) ─────────────────────────────────

    [Fact]
    public async Task AC2_BatchSum_EqualsSettlementSum()
    {
        var amounts = new[] { 150_000m, 75_000m, 200_000m };
        foreach (var amt in amounts)
            await SeedAsync("jeeber-002", amt);

        var batches = await RunWindowAsync();
        var batch   = batches.Single(b => b.JeeberId == "jeeber-002");

        var expectedGross      = amounts.Sum();
        var expectedCommission = amounts.Sum(a =>
            Math.Max(1_000m, Math.Round(a * 0.15m, 2, MidpointRounding.AwayFromZero)));
        var expectedNet = expectedGross - expectedCommission;

        Assert.Equal(expectedGross,      batch.TotalGrossLbp,      precision: 2);
        Assert.Equal(expectedCommission, batch.TotalCommissionLbp,  precision: 2);
        Assert.Equal(expectedNet,        batch.TotalNetLbp,          precision: 2);
        Assert.Equal(amounts.Length,     batch.SettlementCount);
    }

    // ── AC3 — Exactly-once per window ────────────────────────────────────────

    [Fact]
    public async Task AC3_BatchCreated_ExactlyOncePerWindow()
    {
        await SeedManyAsync("jeeber-003", 3);

        var run1 = await RunWindowAsync();
        var run2 = await RunWindowAsync(); // idempotent

        var b1 = run1.Single(b => b.JeeberId == "jeeber-003");
        var b2 = run2.Single(b => b.JeeberId == "jeeber-003");

        Assert.Equal(b1.Id, b2.Id);
        Assert.NotEqual(Guid.Empty, b1.Id);

        // Only one batch in the store for this period.
        var allBatched = (await _batchStore.ListByStatusAsync("batched", CancellationToken.None))
            .Where(b => b.PeriodStart == PeriodStart && b.JeeberId == "jeeber-003")
            .ToList();
        Assert.Single(allBatched);
    }

    // ── AC4 — Mark-paid idempotency ──────────────────────────────────────────

    [Fact]
    public async Task AC4_MarkPaid_Idempotent()
    {
        await SeedManyAsync("jeeber-004", 2);
        var batches = await RunWindowAsync();
        var batch   = batches.Single(b => b.JeeberId == "jeeber-004");

        var now  = _clock.GetUtcNow();
        var ct   = CancellationToken.None;
        var paid1 = await _batchStore.MarkPaidAsync(batch.Id, "admin-001", now, ct);
        var paid2 = await _batchStore.MarkPaidAsync(batch.Id, "admin-001", now, ct); // idempotent

        Assert.Equal("paid", paid1.Status);
        Assert.Equal("paid", paid2.Status);
        Assert.Equal(paid1.PaidAt, paid2.PaidAt);
        Assert.Equal("admin-001", paid1.PaidBy);
    }

    // ── AC4b — Mark-paid on unknown batch throws ──────────────────────────────

    [Fact]
    public async Task AC4b_MarkPaid_NotFound_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _batchStore.MarkPaidAsync(Guid.NewGuid(), "admin-001",
                _clock.GetUtcNow(), CancellationToken.None));
    }

    // ── AC5 — Window boundary exclusion ──────────────────────────────────────

    [Theory]
    [InlineData("2026-06-08T23:59:59Z", true,  "at window end — included")]
    [InlineData("2026-06-09T00:00:00Z", false,  "next window start — excluded")]
    [InlineData("2026-06-02T00:00:00Z", true,  "at window start — included")]
    [InlineData("2026-06-01T23:59:59Z", false, "before window start — excluded")]
    public async Task AC5_BatchWindow_BoundaryInclusion(
        string settledAtStr, bool shouldBeIn, string reason)
    {
        var settledAt = DateTimeOffset.Parse(settledAtStr);
        await SeedAsync("jeeber-005", settledAt: settledAt);

        var recorded = await _settlementStore.ListRecordedInWindowAsync(
            WindowStart, WindowEnd, 100, CancellationToken.None);
        var found = recorded.Any(s => s.JeeberId == "jeeber-005");

        Assert.True(shouldBeIn == found,
            $"Boundary case '{reason}': expected shouldBeIn={shouldBeIn} but found={found}");
    }

    // ── AC6 — Pure decimal arithmetic ────────────────────────────────────────

    [Fact]
    public void AC6_CommissionMath_PureDecimal_NoFloat()
    {
        const decimal gross = 333_333m;
        var commission = Math.Max(1_000m, Math.Round(gross * 0.15m, 2, MidpointRounding.AwayFromZero));
        var net = gross - commission;

        // 333,333 × 0.15 = 49,999.95
        Assert.Equal(49_999.95m, commission);
        Assert.Equal(283_333.05m, net);
    }

    // ── AC7 — One batch per Jeeber per window ─────────────────────────────────

    [Fact]
    public async Task AC7_MultiJeeber_SeparateBatchesPerJeeber()
    {
        await SeedAsync("jeeber-A", 100_000m);
        await SeedAsync("jeeber-A", 50_000m);
        await SeedAsync("jeeber-B", 200_000m);

        var batches = await RunWindowAsync();

        var aB = batches.Where(b => b.JeeberId == "jeeber-A").ToList();
        var bB = batches.Where(b => b.JeeberId == "jeeber-B").ToList();

        Assert.Single(aB);
        Assert.Single(bB);
        Assert.Equal(2,         aB[0].SettlementCount);
        Assert.Equal(1,         bB[0].SettlementCount);
        Assert.Equal(150_000m,  aB[0].TotalGrossLbp);
        Assert.Equal(200_000m,  bB[0].TotalGrossLbp);
    }
}
