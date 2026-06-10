namespace JeebGateway.Financials;

/// <summary>
/// Flat earnings summary (legacy/internal shape). Retained verbatim for
/// backward compatibility with any caller that already reads the flat keys;
/// the canonical mobile/wallet projection is <see cref="EarningsProjection"/>
/// with the nested <c>totals</c> envelope (S10 H5/A4/A5, GAP-4).
/// </summary>
public sealed record EarningsSummary(
    string JeeberId,
    decimal TotalEarnings,
    decimal TotalCommission,
    decimal NetPayout,
    int DeliveryCount,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd);

public sealed record DailyEarnings(
    DateOnly Date,
    decimal Gross,
    decimal Commission,
    decimal Net,
    int Deliveries);

/// <summary>
/// The canonical earnings totals envelope (S10 H5/A4 / JEB-58, BR-16/BR-17).
///
/// <c>net == gross - commission</c> for the period, all in <see cref="Currency"/>
/// (always "LBP"). The wallet copies the per-settlement values verbatim — the
/// gateway performs no rate re-derivation, it only sums the persisted rows.
/// </summary>
public sealed record EarningsTotals(
    decimal Net,
    decimal Gross,
    decimal Commission,
    string Currency);

/// <summary>
/// One settled-delivery line in <see cref="EarningsProjection.Entries"/>. Carries
/// the verbatim persisted gross/commission/net for the delivery (BR-16 — zero
/// arithmetic on the wallet copy).
/// </summary>
public sealed record EarningsEntry(
    string DeliveryId,
    string SettlementId,
    decimal Gross,
    decimal Commission,
    decimal Net,
    string Currency,
    DateTimeOffset SettledAt);

/// <summary>
/// Canonical earnings projection returned by the gateway earnings reads (S10).
/// The mobile + admin surfaces bind <c>totals.{net,gross,commission,currency}</c>
/// and <c>entries[]</c>; the flat <c>jeeberId/totalEarnings/...</c> fields are
/// kept ALONGSIDE (additive) so the legacy shape never breaks.
/// </summary>
public sealed record EarningsProjection(
    string JeeberId,
    EarningsTotals Totals,
    IReadOnlyList<EarningsEntry> Entries,
    int DeliveryCount,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd)
{
    // Legacy flat aliases (additive — same underlying values as Totals.*).
    public decimal TotalEarnings => Totals.Gross;
    public decimal TotalCommission => Totals.Commission;
    public decimal NetPayout => Totals.Net;
}

public interface IEarningsAggregationService
{
    Task<EarningsSummary> GetSummaryAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    /// <summary>
    /// Canonical projection for the period (nested <c>totals</c> + <c>entries[]</c>).
    /// A null window bound means unbounded on that side (lifetime read).
    /// </summary>
    Task<EarningsProjection> GetProjectionAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct);

    Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct);

    /// <summary>Canonical lifetime projection (unbounded window).</summary>
    Task<EarningsProjection> GetLifetimeProjectionAsync(string jeeberId, CancellationToken ct);

    /// <summary>
    /// JEB-58: canonical projection filtered by COD state (e.g. ["batched","paid"]).
    /// Excludes "recorded" settlements which are pending batch and not yet earnings.
    /// </summary>
    Task<EarningsProjection> GetProjectionWithStatesAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<string> codStates,
        CancellationToken ct);
}

/// <summary>
/// Sums the gateway-owned settlement rows into the canonical earnings
/// projection (T-backend-018). This is the in-memory aggregation; the swap to
/// the wallet-service earnings projection (UseUpstreamWalletEarnings) lands
/// behind the same interface without changing the controller.
///
/// <para>net = gross(goodsCost) - commission, per settled delivery; the period
/// totals are the sums. Currency is always LBP (the gateway's single
/// operating currency). No re-arithmetic on the persisted commission (BR-16).</para>
/// </summary>
public sealed class EarningsAggregationService : IEarningsAggregationService
{
    private readonly ISettlementStore _store;

    public EarningsAggregationService(ISettlementStore store) => _store = store;

    public async Task<EarningsProjection> GetProjectionAsync(
        string jeeberId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct)
    {
        var rows = await _store.ListByJeeberAsync(jeeberId, from, to, ct);

        var entries = new List<EarningsEntry>(rows.Count);
        decimal gross = 0m, commission = 0m, net = 0m;
        foreach (var s in rows)
        {
            // gross = the cash value of the goods; net = what the Jeeber keeps
            // (gross - commission). Insurance is a pass-through cost charged to
            // the client, not part of the Jeeber's earnings, so it is excluded
            // from net here (BR-16: earnings = gross - commission).
            var entryNet = s.GoodsCost - s.Commission;
            entries.Add(new EarningsEntry(
                DeliveryId: s.DeliveryId,
                SettlementId: s.Id,
                Gross: s.GoodsCost,
                Commission: s.Commission,
                Net: entryNet,
                Currency: s.Currency,
                SettledAt: s.SettledAt));
            gross += s.GoodsCost;
            commission += s.Commission;
            net += entryNet;
        }

        var periodStart = from ?? (rows.Count > 0 ? rows[0].SettledAt : DateTimeOffset.UnixEpoch);
        var periodEnd = to ?? DateTimeOffset.UtcNow;

        return new EarningsProjection(
            JeeberId: jeeberId,
            Totals: new EarningsTotals(net, gross, commission, SettlementService.CurrencyLbp),
            Entries: entries,
            DeliveryCount: rows.Count,
            PeriodStart: periodStart,
            PeriodEnd: periodEnd);
    }

    public Task<EarningsProjection> GetLifetimeProjectionAsync(string jeeberId, CancellationToken ct)
        => GetProjectionAsync(jeeberId, from: null, to: null, ct);

    public async Task<EarningsSummary> GetSummaryAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var p = await GetProjectionAsync(jeeberId, from, to, ct);
        return new EarningsSummary(
            jeeberId, p.Totals.Gross, p.Totals.Commission, p.Totals.Net,
            p.DeliveryCount, p.PeriodStart, p.PeriodEnd);
    }

    public async Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        var rows = await _store.ListByJeeberAsync(jeeberId, from, to, ct);
        return rows
            .GroupBy(s => DateOnly.FromDateTime(s.SettledAt.UtcDateTime))
            .Select(g => new DailyEarnings(
                Date: g.Key,
                Gross: g.Sum(s => s.GoodsCost),
                Commission: g.Sum(s => s.Commission),
                Net: g.Sum(s => s.GoodsCost - s.Commission),
                Deliveries: g.Count()))
            .OrderBy(d => d.Date)
            .ToList();
    }

    public async Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct)
    {
        var p = await GetLifetimeProjectionAsync(jeeberId, ct);
        return new EarningsSummary(
            jeeberId, p.Totals.Gross, p.Totals.Commission, p.Totals.Net,
            p.DeliveryCount, p.PeriodStart, p.PeriodEnd);
    }

    public async Task<EarningsProjection> GetProjectionWithStatesAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        IReadOnlyCollection<string> codStates,
        CancellationToken ct)
    {
        var rows = await _store.ListByJeeberAsync(jeeberId, from, to, ct, codStates);

        var entries = new List<EarningsEntry>(rows.Count);
        decimal gross = 0m, commission = 0m, net = 0m;
        foreach (var s in rows)
        {
            var entryNet = s.GoodsCost - s.Commission;
            entries.Add(new EarningsEntry(
                DeliveryId:   s.DeliveryId,
                SettlementId: s.Id,
                Gross:        s.GoodsCost,
                Commission:   s.Commission,
                Net:          entryNet,
                Currency:     s.Currency,
                SettledAt:    s.SettledAt));
            gross      += s.GoodsCost;
            commission += s.Commission;
            net        += entryNet;
        }

        return new EarningsProjection(
            JeeberId:      jeeberId,
            Totals:        new EarningsTotals(net, gross, commission, SettlementService.CurrencyLbp),
            Entries:       entries,
            DeliveryCount: rows.Count,
            PeriodStart:   from,
            PeriodEnd:     to);
    }
}
