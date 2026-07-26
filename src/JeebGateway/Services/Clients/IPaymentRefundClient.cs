namespace JeebGateway.Services.Clients;

/// <summary>
/// Refund surface over the locked-in payments path
/// (<c>olivium-dev/unified_payment_gateway</c>). The dispute-case
/// orchestrator (T-BE-028 / JEB-64) calls this exactly when an admin
/// resolves with <c>decision=refund</c>, supplying the case id as the
/// idempotency key so a retried resolve does not double-refund.
///
/// Hand-coded transport. The committed UPG OpenAPI spec and its
/// NSwag-generated client were REMOVED on 2026-07-26 (owner directive: no
/// unified_payment_gateway coupling in Jeeb) — they had zero call sites. This
/// interface is deliberately NOT removed: it is still the LIVE refund path.
/// <see cref="HttpPaymentRefundClient"/> takes over whenever
/// <c>Services:UnifiedPayment:BaseUrl</c> is configured (it IS set in
/// appsettings.Production.json), so a real dispute refund still leaves the
/// gateway for unified_payment_gateway. Removing this requires a replacement
/// refund destination first — see docs/batches/b02-20260726/UPG-REMOVAL.md.
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
