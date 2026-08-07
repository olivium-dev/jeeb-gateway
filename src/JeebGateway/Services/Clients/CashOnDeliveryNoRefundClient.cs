namespace JeebGateway.Services.Clients;

/// <summary>
/// The <see cref="IPaymentRefundClient"/> used by the cash-on-delivery policy.
///
/// <para>Unified-payment-gateway owns the durable cash-on-delivery settlement
/// record. It does not capture a card payment, so there is no automated card
/// refund to reverse when a cash dispute is resolved.</para>
///
/// <para><b>Why this throws instead of returning a benign result.</b> The previous fallback was
/// <c>InMemoryPaymentRefundClient</c>, which <b>reports success</b>. With the BaseUrl absent, every
/// dispute refund (real money OUT) became a no-op the system believed had worked — a money path
/// that lies. That is strictly worse than an outage: an outage is visible, a phantom success is
/// not, and it is exactly the "publisher with no subscriber" failure shape this codebase keeps
/// producing. So the COD client fails loudly.</para>
///
/// <para>If this throws in production, a dispute-resolution path attempted an
/// automated refund for cash already exchanged in person. Cash disputes remain
/// tracked for manual resolution; they must never report a synthetic refund.</para>
/// </summary>
public sealed class CashOnDeliveryNoRefundClient : IPaymentRefundClient
{
    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"Cash-on-delivery has no captured card payment to refund. Refused refund for " +
            $"case '{request.CaseId}' on delivery '{request.DeliveryId}'. Settle this dispute in " +
            $"cash. This is deliberately a hard failure — the previous in-memory fallback " +
            $"reported success without moving money, which made dispute refunds silently fake.");
}
