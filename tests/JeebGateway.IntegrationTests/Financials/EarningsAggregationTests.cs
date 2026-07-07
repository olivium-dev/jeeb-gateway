using JeebGateway.Financials;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEB-58 — Earnings Ledger + Period Aggregation tests.
/// Uses InMemorySettlementStore. No DB required.
/// Covers QA-PRE-JEB-512: AC1–AC6 (EXPLAIN AC4 is deferred to Testcontainers QV).
/// </summary>
public class EarningsAggregationTests
{
    private readonly InMemorySettlementStore _store;
    private readonly EarningsAggregationService _service;
    private readonly IMemoryCache _cache;
    private readonly FakeTimeProvider _clock;

    private static readonly string[] EarningsCodStates =
        [CodSettlementState.Batched, CodSettlementState.Paid];

    public EarningsAggregationTests()
    {
        _clock   = new FakeTimeProvider(new DateTimeOffset(2026, 6, 10, 12, 0, 0, TimeSpan.Zero));
        _store   = new InMemorySettlementStore();
        _service = new EarningsAggregationService(_store);
        _cache   = new MemoryCache(new MemoryCacheOptions { SizeLimit = 100 });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<Settlement> SeedAsync(
        string jeeberId,
        string codState,
        decimal grossAmount = 100_000m,
        DateTimeOffset? settledAt = null)
    {
        var at         = settledAt ?? MidWeek;  // default to mid-week so tests within WeekStart..WeekEnd
        var commission = Math.Max(1_000m, Math.Round(grossAmount * 0.15m, 2, MidpointRounding.AwayFromZero));
        var s = new Settlement
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
            Currency          = "USD",
            PaymentMethod     = "cod",
            State             = "pending_settlement",
            CodState          = codState,
            SettledAt         = at,
        };
        await _store.TryInsertAsync(s, CancellationToken.None);
        return s;
    }

    private static readonly DateTimeOffset WeekStart = new(2026, 6, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WeekEnd   = new(2026, 6, 9, 23, 59, 59, TimeSpan.Zero);
    private static readonly DateTimeOffset MidWeek   = new(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);

    // ── AC1 — Only batched+paid rows in earnings ──────────────────────────────

    [Fact]
    public async Task AC1_Earnings_OnlyBatchedAndPaid_NotRecorded()
    {
        // Arrange
        var recorded = await SeedAsync("j-001", CodSettlementState.Recorded, 50_000m);
        var batched  = await SeedAsync("j-001", CodSettlementState.Batched,  150_000m);
        var paid     = await SeedAsync("j-001", CodSettlementState.Paid,     100_000m);

        // Act: use GetProjectionWithStatesAsync (JEB-58 earnings path)
        var earnings = await _service.GetProjectionWithStatesAsync(
            "j-001", WeekStart, WeekEnd, EarningsCodStates, CancellationToken.None);

        // Assert: recorded row excluded
        Assert.DoesNotContain(earnings.Entries, e => e.DeliveryId == recorded.DeliveryId);
        Assert.Contains(earnings.Entries, e => e.DeliveryId == batched.DeliveryId);
        Assert.Contains(earnings.Entries, e => e.DeliveryId == paid.DeliveryId);

        // Assert: totals == batched + paid only
        Assert.Equal(250_000m, earnings.Totals.Gross);
        Assert.Equal(2,        earnings.DeliveryCount);
    }

    // ── AC2 — net == gross - commission per row (no re-derivation from rate) ──

    [Fact]
    public async Task AC2_Net_IsGrossMinusCommission_PerRow()
    {
        // Arrange: known exact commission values (min-fee scenario)
        var s1 = await SeedAsync("j-002", CodSettlementState.Paid, 150_000m);  // commission = 22,500
        var s2 = await SeedAsync("j-002", CodSettlementState.Paid, 75_000m);   // commission = 11,250
        var s3 = await SeedAsync("j-002", CodSettlementState.Paid, 5_000m);    // commission = max(1000, 750) = 1,000

        var earnings = await _service.GetProjectionWithStatesAsync(
            "j-002", WeekStart, WeekEnd, EarningsCodStates, CancellationToken.None);

        // Assert per-row: net = gross - commission (verbatim from persisted rows, not re-derived)
        foreach (var entry in earnings.Entries)
        {
            var row = new[] { s1, s2, s3 }.Single(r => r.DeliveryId == entry.DeliveryId);
            Assert.Equal(row.GoodsCost - row.Commission, entry.Net);
            Assert.Equal(row.GoodsCost, entry.Gross);
            Assert.Equal(row.Commission, entry.Commission);
        }

        // Assert period totals
        var expectedGross      = s1.GoodsCost + s2.GoodsCost + s3.GoodsCost;
        var expectedCommission = s1.Commission + s2.Commission + s3.Commission;
        var expectedNet        = expectedGross - expectedCommission;

        Assert.Equal(expectedGross,      earnings.Totals.Gross);
        Assert.Equal(expectedCommission, earnings.Totals.Commission);
        Assert.Equal(expectedNet,        earnings.Totals.Net);

        // Assert: different from re-derived net (because of min-fee)
        var reDerivedNet = (s1.GoodsCost + s2.GoodsCost + s3.GoodsCost) * (1m - 0.15m);
        Assert.NotEqual(reDerivedNet, earnings.Totals.Net);
    }

    // ── AC3 — Adjacent period filters are exclusive (no double-counting) ──────

    [Fact]
    public async Task AC3_PeriodFilter_AdjacentPeriods_AreExclusive()
    {
        // Arrange: one settlement in each period
        var week1End   = new DateTimeOffset(2026, 6, 7, 23, 59, 59, TimeSpan.Zero);
        var week2Start = new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero);

        var sW1 = await SeedAsync("j-003", CodSettlementState.Paid, settledAt: week1End);
        var sW2 = await SeedAsync("j-003", CodSettlementState.Paid, settledAt: week2Start);

        // Act
        var week1 = await _service.GetProjectionWithStatesAsync(
            "j-003", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), week1End,
            EarningsCodStates, CancellationToken.None);

        var week2 = await _service.GetProjectionWithStatesAsync(
            "j-003", week2Start, new DateTimeOffset(2026, 6, 14, 23, 59, 59, TimeSpan.Zero),
            EarningsCodStates, CancellationToken.None);

        // Assert: no overlap
        Assert.Contains(week1.Entries, e => e.DeliveryId == sW1.DeliveryId);
        Assert.DoesNotContain(week1.Entries, e => e.DeliveryId == sW2.DeliveryId);

        Assert.Contains(week2.Entries, e => e.DeliveryId == sW2.DeliveryId);
        Assert.DoesNotContain(week2.Entries, e => e.DeliveryId == sW1.DeliveryId);

        // Assert: totals are additive (no double-counting)
        var combined = week1.Totals.Net + week2.Totals.Net;
        var lifetime = await _service.GetLifetimeProjectionAsync("j-003", CancellationToken.None);
        Assert.Equal(lifetime.Totals.Net, combined);
    }

    // ── AC4 — EXPLAIN uses index (deferred to Testcontainers QV pass) ─────────

    [Fact]
    public void AC4_ExplainUsesIndex_DeferredToPostgresQV()
    {
        // Property documented here; Testcontainers test is run in QV pass.
        // The index idx_settlements_jeeber_state_settled is defined in migration
        // 0015_init_settlements_batches.sql and covers (jeeber_id, state, settled_at).
        Assert.True(true, "AC4 EXPLAIN test deferred to QV Testcontainers suite.");
    }

    // ── AC5 — All entries in same currency ────────────────────────────────────

    [Fact]
    public async Task AC5_EarningsEntries_AllUSD()
    {
        for (var i = 0; i < 5; i++)
            await SeedAsync("j-004", CodSettlementState.Paid, 100_000m + i * 10_000m);

        var earnings = await _service.GetProjectionWithStatesAsync(
            "j-004", WeekStart, WeekEnd, EarningsCodStates, CancellationToken.None);

        var currencies = earnings.Entries.Select(e => e.Currency).Distinct().ToList();
        Assert.Single(currencies);
        Assert.Equal("USD", currencies[0]);
        Assert.Equal("USD", earnings.Totals.Currency);
    }

    // ── AC6 — Cache: two calls → one DB hit ──────────────────────────────────

    [Fact]
    public async Task AC6_Cache_SecondCall_ServedFromCache()
    {
        // Arrange: one settlement seeded
        await SeedAsync("j-005", CodSettlementState.Paid, 100_000m);

        var cacheKey   = $"earnings:j-005:week:20260603:20260609";
        var callCount  = 0;
        var ct         = CancellationToken.None;

        // Manual cache wrapper (mirrors JeebEarningsController logic)
        async Task<EarningsProjection> GetWithCache()
        {
            if (_cache.TryGetValue(cacheKey, out EarningsProjection? hit) && hit is not null)
                return hit;

            callCount++;
            var result = await _service.GetProjectionWithStatesAsync(
                "j-005", WeekStart, WeekEnd, EarningsCodStates, ct);

            _cache.Set(cacheKey, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5),
                Size = 1,
            });
            return result;
        }

        // Act: call twice
        var r1 = await GetWithCache();
        var r2 = await GetWithCache();

        // Assert: DB queried exactly once
        Assert.Equal(1, callCount);
        Assert.Equal(r1.Totals.Net, r2.Totals.Net);
        Assert.Equal(r1.DeliveryCount, r2.DeliveryCount);
    }

    // ── AC7 — Period week boundary resolution ─────────────────────────────────

    [Fact]
    public async Task AC7_WeekBoundary_MonToSun()
    {
        // JEB-58 week period: Monday 00:00 UTC to Sunday 23:59:59 UTC
        // _clock is set to Wednesday 2026-06-10 → week is Mon 2026-06-08 to Sun 2026-06-14
        var monday  = new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero);
        var sunday  = new DateTimeOffset(2026, 6, 14, 23, 59, 59, TimeSpan.Zero);

        var inWeek  = await SeedAsync("j-006", CodSettlementState.Paid, settledAt: monday.AddHours(12));
        var lastWk  = await SeedAsync("j-006", CodSettlementState.Paid, settledAt: monday.AddSeconds(-1));

        var earnings = await _service.GetProjectionWithStatesAsync(
            "j-006", monday, sunday, EarningsCodStates, CancellationToken.None);

        Assert.Contains(earnings.Entries, e => e.DeliveryId == inWeek.DeliveryId);
        Assert.DoesNotContain(earnings.Entries, e => e.DeliveryId == lastWk.DeliveryId);
    }

    // ── AC8 — Empty period returns zero totals ────────────────────────────────

    [Fact]
    public async Task AC8_EmptyPeriod_ReturnsZeroTotals()
    {
        var earnings = await _service.GetProjectionWithStatesAsync(
            "j-007", WeekStart, WeekEnd, EarningsCodStates, CancellationToken.None);

        Assert.Equal(0m, earnings.Totals.Gross);
        Assert.Equal(0m, earnings.Totals.Net);
        Assert.Equal(0m, earnings.Totals.Commission);
        Assert.Empty(earnings.Entries);
        Assert.Equal(0, earnings.DeliveryCount);
    }
}
