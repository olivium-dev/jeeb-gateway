namespace JeebGateway.Financials;

/// <summary>
/// Settlement lifecycle states (T-backend-016 / JEEB-34).
///
/// pending_settlement → settled → receipt_generated
///
/// A delivery enters <see cref="PendingSettlement"/> the moment the OTP
/// handover completes (status = delivered). The Jeeber records the cash
/// they collected via POST /deliveries/{id}/settle which flips the row to
/// <see cref="Settled"/> and posts the ledger entry to wallet-service.
/// GET /deliveries/{id}/receipt returns the rendered receipt and stamps
/// <see cref="ReceiptGenerated"/> once the first read happens.
/// </summary>
public static class SettlementState
{
    public const string PendingSettlement = "pending_settlement";
    public const string Settled = "settled";
    public const string ReceiptGenerated = "receipt_generated";
}

/// <summary>
/// Persisted settlement row. One per delivery — the store enforces
/// uniqueness on <see cref="DeliveryId"/> so a re-submitted settle call is
/// idempotent rather than duplicating ledger entries.
/// </summary>
public sealed class Settlement
{
    public required string Id { get; init; }
    public required string DeliveryId { get; init; }
    public required string ClientId { get; init; }
    public required string JeeberId { get; init; }
    public required string TierId { get; init; }
    public required decimal GoodsCost { get; init; }
    public required CommissionTier CommissionTier { get; init; }
    public required decimal CommissionRate { get; init; }
    public required decimal Commission { get; init; }
    public required decimal Insurance { get; init; }
    public required decimal Total { get; init; }
    public required bool MinimumFeeApplied { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }
    public required string State { get; set; }
    public required DateTimeOffset SettledAt { get; init; }
    public DateTimeOffset? ReceiptGeneratedAt { get; set; }
    public string? LedgerEntryId { get; set; }
}

/// <summary>
/// POST /deliveries/{id}/settle body. The Jeeber records the cash they
/// collected at hand-off; the gateway re-computes commission + insurance
/// from <see cref="GoodsCost"/> and the row's tier — the caller never
/// gets to choose the rate.
/// </summary>
public sealed class SettleDeliveryRequest
{
    /// <summary>Cash value of the goods the Jeeber collected, in LBP.</summary>
    public decimal GoodsCost { get; set; }

    /// <summary>
    /// Only "cash" is accepted in MVP — the gateway's payment policy
    /// routes card transactions through unified_payment_gateway. Defaults
    /// to "cash" when omitted.
    /// </summary>
    public string? PaymentMethod { get; set; }
}

/// <summary>
/// Successful settle response. Returns the full fee breakdown alongside
/// the persisted settlement id so the mobile app can show the Jeeber the
/// numbers immediately.
/// </summary>
public sealed class SettleDeliveryResponse
{
    public required string SettlementId { get; init; }
    public required string DeliveryId { get; init; }
    public required string State { get; init; }
    public required decimal GoodsCost { get; init; }
    public required string CommissionTier { get; init; }
    public required decimal CommissionRate { get; init; }
    public required decimal Commission { get; init; }
    public required decimal Insurance { get; init; }
    public required decimal Total { get; init; }
    public required bool MinimumFeeApplied { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }
    public required DateTimeOffset SettledAt { get; init; }
    public string? LedgerEntryId { get; init; }
}

/// <summary>
/// GET /deliveries/{id}/receipt response. Rendered server-side so every
/// mobile and admin surface sees identical totals.
/// </summary>
public sealed class ReceiptResponse
{
    public required string ReceiptNumber { get; init; }
    public required string DeliveryId { get; init; }
    public required string SettlementId { get; init; }
    public required string ClientId { get; init; }
    public required string JeeberId { get; init; }
    public required string TierId { get; init; }
    public required string CommissionTier { get; init; }
    public required IReadOnlyList<ReceiptLine> Lines { get; init; }
    public required decimal Total { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
}

public sealed record ReceiptLine(string Label, decimal Amount);

/// <summary>
/// Outcome shape returned by <see cref="ISettlementService"/>. Mirrors
/// the cancellation / OTP pattern used elsewhere in the gateway —
/// controllers translate each enum value to a ProblemDetails or 200.
/// </summary>
public enum SettlementOutcome
{
    Settled,
    AlreadySettled,
    DeliveryNotFound,
    NotDelivered,
    NotAuthorized,
    InvalidAmount,
    InvalidPaymentMethod
}

public sealed record SettlementResult(
    SettlementOutcome Outcome,
    Settlement? Settlement,
    string? Reason);
