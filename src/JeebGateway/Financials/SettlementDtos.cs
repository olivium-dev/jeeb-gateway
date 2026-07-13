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

    // JEB-56/57: COD platform batch lifecycle (additive fields — null until batched).
    public Guid? BatchId { get; set; }
    public DateTimeOffset? BatchedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }

    /// <summary>Platform state in the COD settlement lifecycle (recorded|batched|paid).</summary>
    public string CodState { get; set; } = CodSettlementState.Recorded;
}

/// <summary>
/// POST /deliveries/{id}/settle body. The Jeeber records the cash they
/// collected at hand-off. The gateway re-computes the flat commission from
/// the delivery row's accepted-offer amount and tier; the caller never gets to
/// choose the commission base or rate.
/// </summary>
public sealed class SettleDeliveryRequest
{
    /// <summary>Cash value of the accepted offer amount the Jeeber collected, in USD.</summary>
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

    /// <summary>
    /// The COD cash the Jeeber collected at hand-off (the accepted-offer amount).
    /// Explicit top-level field so mobile/admin can render the receipt header
    /// without re-parsing the itemised <see cref="Lines"/>. Equal to the
    /// settlement's goods cost.
    /// </summary>
    public required decimal CodAmount { get; init; }

    /// <summary>The flat commission the platform charged (COD × <see cref="CommissionRate"/>).</summary>
    public required decimal Commission { get; init; }

    /// <summary>The commission rate applied — flat 10% for every Jeeb tier.</summary>
    public required decimal CommissionRate { get; init; }

    /// <summary>
    /// What the Jeeber nets from the COD after the platform commission is
    /// deducted (<see cref="CodAmount"/> − <see cref="Commission"/>). This is the
    /// payout the Jeeber keeps against the cash they already hold.
    /// </summary>
    public required decimal Payout { get; init; }

    public required decimal Total { get; init; }
    public required string Currency { get; init; }
    public required string PaymentMethod { get; init; }
    public required DateTimeOffset IssuedAt { get; init; }
}

public sealed record ReceiptLine(string Label, decimal Amount);

/// <summary>
/// GET /v1/deliveries/{id}/settlement response (S09 H8 / JEB-54).
///
/// The settlement-intent READ surface. S09 asserts only that a single
/// settlement intent exists for the delivery — the commission window opened
/// at handover (Done) — and that it is idempotent on <see cref="DeliveryId"/>.
/// The fee math + ledger posting are the S10 concern, exposed by the
/// POST /deliveries/{id}/settle action; this read never mutates and never
/// double-creates. <see cref="State"/> is:
/// <list type="bullet">
///   <item><c>pending_settlement</c> — Done reached, no cash recorded yet
///         (the open window the Jeeber will settle against).</item>
///   <item><c>settled</c> / <c>receipt_generated</c> — the Jeeber has already
///         recorded the cash (reflects the persisted row verbatim).</item>
/// </list>
/// </summary>
public sealed class SettlementIntentResponse
{
    public required string DeliveryId { get; init; }
    public required string State { get; init; }

    /// <summary>
    /// True once the delivery has reached the settle-able terminal state and
    /// the commission intent is open. S09 asserts the enqueue exists; this is
    /// the machine-readable form of "settlement queued / window open".
    /// </summary>
    public required bool Created { get; init; }

    /// <summary>Persisted settlement id once the Jeeber has settled; null while pending.</summary>
    public string? SettlementId { get; init; }

    /// <summary>The fee total once settled; null while the intent is still pending.</summary>
    public decimal? Total { get; init; }

    public string? Currency { get; init; }
}

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
