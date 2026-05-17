namespace JeebGateway.Financials;

public sealed record FinanceDashboardSummary(
    decimal TotalRevenue,
    decimal TotalCommission,
    decimal TotalPayouts,
    int TotalSettlements,
    int PendingSettlements,
    DateTimeOffset AsOf);

public sealed record TopJeeberEarning(
    string JeeberId,
    string? DisplayName,
    decimal TotalEarnings,
    int DeliveryCount);

public interface IAdminFinanceDashboardService
{
    Task<FinanceDashboardSummary> GetDashboardAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct);

    Task<IReadOnlyList<TopJeeberEarning>> GetTopEarnersAsync(
        int limit,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct);
}

public sealed class AdminFinanceDashboardService : IAdminFinanceDashboardService
{
    public Task<FinanceDashboardSummary> GetDashboardAsync(
        DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        return Task.FromResult(new FinanceDashboardSummary(
            0m, 0m, 0m, 0, 0, DateTimeOffset.UtcNow));
    }

    public Task<IReadOnlyList<TopJeeberEarning>> GetTopEarnersAsync(
        int limit, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<TopJeeberEarning>>(
            Array.Empty<TopJeeberEarning>());
    }
}
