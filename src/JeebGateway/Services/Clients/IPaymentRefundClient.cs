namespace JeebGateway.Services.Clients;

/// <summary>
/// Refund surface over the locked-in payments path
/// (<c>olivium-dev/unified_payment_gateway</c>). The dispute-case
/// orchestrator (T-BE-028 / JEB-64) calls this exactly when an admin
/// resolves with <c>decision=refund</c>, supplying the case id as the
/// idempotency key so a retried resolve does not double-refund.
///
/// Hand-coded ahead of the NSwag-generated client — the
/// <c>unified_payment_gateway</c> OpenAPI spec lives in
/// <c>contracts/unified-payment-gateway.openapi.json</c> as a placeholder
/// until the upstream spec is published. Replace this implementation
/// with the NSwag-generated client during the T-backend-bff-payments
/// follow-up.
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
    /// Caller-supplied <c>Idempotency-Key</c>. <c>unified_payment_gateway</c>
    /// returns the existing refund entry when the same key is replayed
    /// so retries are safe. The dispute service uses
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
