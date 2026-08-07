namespace JeebGateway.Services.Clients;

/// <summary>
/// Refund surface for the dispute-case orchestrator (T-BE-028 / JEB-64), called
/// exactly when an admin resolves with <c>decision=refund</c>, supplying the case
/// id as the idempotency key so a retried resolve does not double-refund.
///
/// <para>Unified-payment-gateway owns durable COD settlement tracking, but COD
/// creates no reversible card capture. The registered implementation is
/// therefore <see cref="CashOnDeliveryNoRefundClient"/> and fails closed.</para>
///
/// <para>The interface is deliberately KEPT rather than deleted along with its
/// transport. Deleting it would silently erase the call sites in
/// <c>Disputes/V2/DisputeCaseService</c>, turning "we cannot refund" into "no
/// refund was ever requested". Keeping it means the request is still made and
/// still FAILS LOUDLY, which is the honest outcome: cash was handed over in
/// person, so there is no capture to reverse. A replacement refund destination
/// requires an explicit, separately designed cash-reimbursement workflow.</para>
/// </summary>
public interface IPaymentRefundClient
{
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct);
}

public sealed class RefundRequest
{
    public required string DeliveryId { get; init; }
    public required string CaseId { get; init; }
    public required decimal AmountUsd { get; init; }
    public required string Reason { get; init; }

    /// <summary>
    /// Caller-supplied <c>Idempotency-Key</c>, retained so a future refund
    /// destination can replay safely. The dispute service uses
    /// <c>"dispute:{caseId}:refund"</c>.
    /// </summary>
    public required string IdempotencyKey { get; init; }
}

public sealed class RefundResult
{
    public required bool Success { get; init; }
    public string? LedgerEntryId { get; init; }
    public string? FailureReason { get; init; }
}
