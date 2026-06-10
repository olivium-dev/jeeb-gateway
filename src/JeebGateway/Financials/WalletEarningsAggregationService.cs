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
/// "year"). Commission is derived at the standard Jeeb rate (15%) so the
/// projection is consistent with the gateway's own settlement accounting
/// (BR-16 — no re-arithmetic on the wallet copy).</para>
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

    private static decimal DeriveCommission(decimal gross) =>
        Math.Round(gross * CommissionCalculator.StandardRate, 2, MidpointRounding.AwayFromZero);

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
