using JeebGateway.Infrastructure;

namespace JeebGateway.Financials;

public interface ISettlementBatchStore
{
    Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct);
    Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct);
    Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct);
    Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct);
    Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct);
    Task<SettlementBatch> MarkPaidAsync(
        Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct);
}

public sealed class SettlementBatch
{
    public Guid Id { get; init; }
    public required string JeeberId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public decimal TotalGrossUsd { get; set; }
    public decimal TotalCommissionUsd { get; set; }
    public decimal TotalNetUsd { get; set; }
    public int SettlementCount { get; set; }
    public string Currency { get; init; } = "USD";
    public string Status { get; set; } = "open";
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Wallet-service does not yet expose the legacy weekly-batch contract. This
/// explicit gate prevents the gateway from creating a replacement batch ledger.
/// </summary>
public sealed class UnavailableSettlementBatchStore : ISettlementBatchStore
{
    public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct) => Fail<IReadOnlyList<Settlement>>();
    public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct) => Fail();
    public Task<SettlementBatch> CreateOrGetBatchAsync(string jeeberId, DateOnly periodStart, DateOnly periodEnd, IReadOnlyList<Settlement> settlements, CancellationToken ct) => Fail<SettlementBatch>();
    public Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct) => Fail<SettlementBatch?>();
    public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct) => Fail<IReadOnlyList<SettlementBatch>>();
    public Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct) => Fail<SettlementBatch>();

    private static OwnerCapabilityUnavailableException Error() =>
        new("wallet-service COD settlement batches");
    private static Task Fail() => Task.FromException(Error());
    private static Task<T> Fail<T>() => Task.FromException<T>(Error());
}
