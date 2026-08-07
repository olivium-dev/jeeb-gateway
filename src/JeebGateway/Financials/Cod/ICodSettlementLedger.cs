namespace JeebGateway.Financials.Cod;

/// <summary>
/// The COD settlement + admin-batch ledger contract (S10 H3.3/H4/N10-N12).
///
/// <para>Cash-on-delivery does not mean process-local accounting. The gateway
/// authorizes and orchestrates; unified-payment-gateway is the durable owner of
/// intents, payable settlements, batches, audit events, and idempotency.</para>
///
/// <para>The gateway keeps the generic COD wire contract stable and authorizes the
/// user JWT before forwarding any request to the owner.</para>
/// </summary>
public interface ICodSettlementLedger
{
    /// <summary>
    /// PUT /api/v1/payments/cod/intents/{externalReference} — durably records a
    /// non-payable delivery snapshot. Intent rows are excluded from settlement
    /// batching until <see cref="FinalizeCodIntentAsync"/> advances them.
    /// </summary>
    Task<CodLedgerResult> UpsertCodIntentAsync(CodIntentRequest request, CancellationToken ct);

    /// <summary>
    /// POST /api/v1/payments/cod/intents/{externalReference}/finalize — atomically
    /// advances a durable intent to the owner's payable pending state.
    /// </summary>
    Task<CodLedgerResult> FinalizeCodIntentAsync(CodFinalizeIntentRequest request, CancellationToken ct);

    /// <summary>POST /api/v1/payments/cod/record — records the COD intent for a settled delivery.</summary>
    Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct);

    /// <summary>GET /api/v1/payments/cod/by-delivery/{deliveryId} — reads the COD record.</summary>
    Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct);

    /// <summary>POST /admin/v1/settlements/{batchId}/mark-paid — bank-confirmation, fronted by the gateway admin gate.</summary>
    Task<CodLedgerResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct);
}

/// <summary>
/// The ledger's HTTP-shaped outcome. The compose controller re-emits the status
/// code and JSON body unchanged so the wire contract published to clients is
/// never reshaped by the owner boundary. <see cref="Available"/> distinguishes
/// an owner response from a transport failure and drives the gateway's 502 path.
/// </summary>
public sealed record CodLedgerResult(bool Available, int StatusCode, string? Body, string ContentType);

/// <summary>
/// COD record request body. The gateway forwards values from the owner projection
/// already computed (gross, flat-10% commission, currency) — the caller never
/// chooses the commission (BR-16).
/// </summary>
public sealed record CodRecordRequest(
    string DeliveryId,
    string JeeberId,
    decimal GrossAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    string Currency,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Restart-safe, non-payable COD snapshot. SnapshotSequence is monotonic for
/// the external reference; the gateway uses 1 for the AtDoor intent.
/// </summary>
public sealed record CodIntentRequest(
    string ExternalReference,
    string ProviderId,
    decimal GrossAmount,
    decimal CommissionRate,
    string Currency,
    long SnapshotSequence,
    IReadOnlyDictionary<string, string>? Metadata = null);

/// <summary>
/// Final owner transition. ExpectedVersion protects the intent observed by the
/// gateway and the idempotency key makes a completion replay exact.
/// </summary>
public sealed record CodFinalizeIntentRequest(
    string ExternalReference,
    string ProviderId,
    decimal GrossAmount,
    decimal CommissionRate,
    string Currency,
    int ExpectedVersion,
    long SnapshotSequence,
    string IdempotencyKey,
    IReadOnlyDictionary<string, string>? Metadata = null);
