namespace JeebGateway.Services.Clients;

/// <summary>
/// The <see cref="IPaymentRefundClient"/> Jeeb runs under the COD-only payments policy.
///
/// <para>OWNER RULING, 2026-07-27: <i>"do not use UPG, jeeb is only cash on delivery"</i>. Jeeb
/// settles cash on delivery, so no card payment is ever captured and there is nothing for a
/// payment gateway to refund. The <c>Services:UnifiedPayment:BaseUrl</c> key — which held
/// <c>http://192.168.2.50:10066</c>, the last live <c>.50</c> destination in committed config — has
/// been removed and must not be re-added.</para>
///
/// <para><b>Why this throws instead of returning a benign result.</b> The previous fallback was
/// <c>InMemoryPaymentRefundClient</c>, which <b>reports success</b>. With the BaseUrl absent, every
/// dispute refund (real money OUT) became a no-op the system believed had worked — a money path
/// that lies. That is strictly worse than an outage: an outage is visible, a phantom success is
/// not, and it is exactly the "publisher with no subscriber" failure shape this codebase keeps
/// producing. So the COD client fails loudly.</para>
///
/// <para>If this throws in production it is not a bug in this class — it means a dispute-resolve
/// path still tries to move money through a payment gateway under a cash-only policy. Fix the
/// caller. Remaining call sites are tracked in
/// <c>docs/batches/b02-20260726/UPG-REMOVAL.md</c>.</para>
/// </summary>
public sealed class CashOnDeliveryNoRefundClient : IPaymentRefundClient
{
    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct) =>
        throw new InvalidOperationException(
            $"Jeeb is cash-on-delivery only (owner ruling 2026-07-27): there is no captured " +
            $"payment to refund, so no payment-gateway refund route exists. Refused refund for " +
            $"case '{request.CaseId}' on delivery '{request.DeliveryId}'. Settle this dispute in " +
            $"cash. This is deliberately a hard failure — the previous in-memory fallback " +
            $"reported success without moving money, which made dispute refunds silently fake. " +
            $"See docs/batches/b02-20260726/UPG-REMOVAL.md.");
}
