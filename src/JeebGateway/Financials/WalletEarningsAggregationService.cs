using JeebGateway.service.ServiceWallet;

namespace JeebGateway.Financials;

/// <summary>
/// Wallet-service–backed <see cref="IEarningsAggregationService"/> (JEB-1434 /
/// JEB-1465). Activated when <c>FeatureFlags:UseUpstream:Earnings=true</c>; the
/// flag-gated switch in Program.cs swaps this in behind the interface without
/// touching the controller.
///
/// <para>Data source: <c>ServiceWalletClient.CreditRevenueAsync</c>
/// (<c>GET Transaction/holder/{holderId}/credit-revenue</c>) returns the gross
/// credited amount for a wallet holder over a named period ("week" / "month" /
/// "year"). Commission is derived via <see cref="CommissionCalculator"/> at the
/// standard Jeeb tier (15%) — including its 1,000 LBP minimum-fee floor — so the
/// wallet-visible commission equals the figure settlement actually deducts
/// (BR-16 — no divergent re-arithmetic on the wallet copy; JEBV4-119 / JEBV4-43).</para>
///
/// <para>Limitation: the wallet endpoint exposes a period total, not
/// per-delivery lines. <see cref="EarningsProjection.Entries"/> is omitted
/// (empty) and <see cref="EarningsProjection.DeliveryCount"/> is 0 when the
/// gross is zero; these are populated by the full settlement flow once
/// settlement rows exist (T-backend-017). A single synthetic summary entry is
/// returned when gross &gt; 0 so the mobile totals surface renders correctly
/// (GAP-4 / H5 binding on <c>totals.{net,gross,commission}</c>).</para>
/// </summary>
public sealed class WalletEarningsAggregationService : IEarningsAggregationService
{
    private readonly ServiceWalletClient _wallet;

    public WalletEarningsAggregationService(ServiceWalletClient wallet)
    {
        _wallet = wallet;
    }

    public async Task<EarningsProjection> GetProjectionAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        var start = from ?? DateTimeOffset.UtcNow.AddDays(-30);
        var end = to ?? DateTimeOffset.UtcNow;

        var gross = await FetchGrossAsync(jeeberId, start, end, ct);
        return BuildProjection(jeeberId, gross, start, end);
    }

    public Task<EarningsProjection> GetProjectionWithStatesAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<string> codStates,
        CancellationToken ct)
    {
        // Wallet-service earnings endpoint does not expose COD-state filtering —
        // delegate to the standard window projection. The JEB-58 state filter
        // applies only to the in-memory aggregation path used in dev/test.
        return GetProjectionAsync(jeeberId, from, to, ct);
    }

    public Task<EarningsProjection> GetLifetimeProjectionAsync(string jeeberId, CancellationToken ct)
    {
        var start = DateTimeOffset.UnixEpoch;
        var end = DateTimeOffset.UtcNow;
        return FetchGrossAsync(jeeberId, start, end, ct)
            .ContinueWith(t => BuildProjection(jeeberId, t.Result, start, end),
                ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    public async Task<EarningsSummary> GetSummaryAsync(
        string jeeberId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var p = await GetProjectionAsync(jeeberId, from, to, ct);
        return ToSummary(jeeberId, p);
    }

    public async Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct)
    {
        var p = await GetLifetimeProjectionAsync(jeeberId, ct);
        return ToSummary(jeeberId, p);
    }

    public async Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(
        string jeeberId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        // CreditRevenueAsync returns a period total only — daily per-delivery breakdown
        // is not surfaced by this wallet endpoint. Return the period total collapsed to
        // the end date so callers get a non-empty list when earnings exist.
        var gross = await FetchGrossAsync(jeeberId, from, to, ct);
        if (gross == 0m) return Array.Empty<DailyEarnings>();

        var commission = DeriveCommission(gross);
        return new[]
        {
            new DailyEarnings(
                Date: DateOnly.FromDateTime(to.UtcDateTime),
                Gross: gross,
                Commission: commission,
                Net: gross - commission,
                Deliveries: 0)
        };
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private async Task<decimal> FetchGrossAsync(
        string jeeberId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var (period, startDate) = MapPeriod(from, to);
        try
        {
            var raw = await _wallet.CreditRevenueAsync(jeeberId, period, startDate, ct);
            return (decimal)raw;
        }
        catch (ApiException)
        {
            // Best-effort: wallet unreachable → return zero (mirrors InMemorySettlementStore
            // behaviour on a cold start before any deliveries are settled).
            return 0m;
        }
    }

    private static EarningsProjection BuildProjection(
        string jeeberId, decimal gross, DateTimeOffset start, DateTimeOffset end)
    {
        var commission = DeriveCommission(gross);
        var net = gross - commission;

        IReadOnlyList<EarningsEntry> entries = gross > 0
            ? new[]
            {
                new EarningsEntry(
                    DeliveryId: string.Empty,
                    SettlementId: string.Empty,
                    Gross: gross,
                    Commission: commission,
                    Net: net,
                    Currency: SettlementService.CurrencyLbp,
                    SettledAt: end)
            }
            : Array.Empty<EarningsEntry>();

        return new EarningsProjection(
            JeeberId: jeeberId,
            Totals: new EarningsTotals(net, gross, commission, SettlementService.CurrencyLbp),
            Entries: entries,
            DeliveryCount: 0,
            PeriodStart: start,
            PeriodEnd: end);
    }

    /// <summary>
    /// Derives the commission for a wallet-projection gross figure so it matches
    /// the commission settlement actually deducts (JEBV4-119 / JEBV4-43, BR-16 —
    /// no divergent re-arithmetic on the wallet copy).
    ///
    /// <para>Reuses <see cref="CommissionCalculator.Calculate"/> as the single
    /// source of truth for the rate (Standard tier = 15%), the
    /// <see cref="CommissionCalculator.MinCommissionLbp"/> minimum-fee floor and
    /// the <c>Math.Round(v, 2, AwayFromZero)</c> rule, so the two sources can no
    /// longer drift. Previously this hardcoded a floorless <c>gross * 0.15</c>,
    /// which under-reported commission below the 6,666.67 LBP breakeven and thus
    /// OVERSTATED a jeeber's wallet-visible net earnings vs the real payout.</para>
    ///
    /// <para>A zero (or non-positive) gross means an empty period with no settled
    /// deliveries — hence no commission. The per-delivery floor must NOT be
    /// applied here, otherwise an idle jeeber would show a phantom
    /// <see cref="CommissionCalculator.MinCommissionLbp"/> charge and a negative
    /// net. This matches the settlement-backed
    /// <see cref="EarningsAggregationService"/>, which sums zero rows to a zero
    /// commission.</para>
    /// </summary>
    private static decimal DeriveCommission(decimal gross)
    {
        if (gross <= 0m) return 0m;

        return CommissionCalculator.Calculate(gross, CommissionTier.Standard).Commission;
    }

    private static EarningsSummary ToSummary(string jeeberId, EarningsProjection p) =>
        new(jeeberId, p.Totals.Gross, p.Totals.Commission, p.Totals.Net,
            p.DeliveryCount, p.PeriodStart, p.PeriodEnd);

    /// <summary>
    /// Maps a from/to window to a wallet-service period name and start date.
    /// The wallet API accepts "week", "month", and "year".
    /// </summary>
    private static (string Period, DateTimeOffset StartDate) MapPeriod(
        DateTimeOffset from, DateTimeOffset to)
    {
        var span = to - from;
        if (span.TotalDays <= 7) return ("week", from);
        if (span.TotalDays <= 31) return ("month", from);
        return ("year", from);
    }
}
