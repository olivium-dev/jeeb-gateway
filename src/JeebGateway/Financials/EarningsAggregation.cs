namespace JeebGateway.Financials;

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

public interface IEarningsAggregationService
{
    Task<EarningsSummary> GetSummaryAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct);

    Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct);
}

public sealed class EarningsAggregationService : IEarningsAggregationService
{
    private readonly ISettlementStore _store;

    public EarningsAggregationService(ISettlementStore store) => _store = store;

    public Task<EarningsSummary> GetSummaryAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        return Task.FromResult(new EarningsSummary(
            jeeberId, 0m, 0m, 0m, 0, from, to));
    }

    public Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(
        string jeeberId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<DailyEarnings>>(Array.Empty<DailyEarnings>());
    }

    public Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct)
    {
        var epoch = DateTimeOffset.UnixEpoch;
        return Task.FromResult(new EarningsSummary(
            jeeberId, 0m, 0m, 0m, 0, epoch, DateTimeOffset.UtcNow));
    }
}
