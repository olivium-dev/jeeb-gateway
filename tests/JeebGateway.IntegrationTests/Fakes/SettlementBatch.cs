// W2-R11 (86be7ca) deleted this row with WeeklySettlementBatch.cs — batches are settlement-service's
// now. TEST DOUBLE only, copied field-for-field from 86be7ca^ so the fixture cannot invent a shape.

namespace JeebGateway.Financials;

/// <summary>
/// Retired gateway settlement batch (JEB-57, TL-PIN-JEB-498). Lives in the test
/// assembly so TestWebApplicationFactory's batch-store fake still compiles; the
/// production assembly must not have it — SettlementServiceCutoverW2R11Tests.D1
/// asserts exactly that and is unaffected by this type.
/// </summary>
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
