namespace JeebGateway.Financials.Cod;

/// <summary>
/// The COD settlement + admin-batch ledger contract (S10 H3.3/H4/N10-N12).
///
/// <para>OWNER RULING 2026-07-27 — "jeeb is only cash on delivery". This contract
/// formerly named <c>IUnifiedPaymentCodClient</c> and had two implementations: an
/// HTTP one that dialed unified_payment_gateway and an in-memory "fallback". The
/// HTTP implementation is deleted; under a cash-only policy there is no external
/// settlement destination to dial, so the in-process implementation
/// (<see cref="InProcessCodSettlementLedger"/>) is the ledger of record rather
/// than a stand-in.</para>
///
/// <para>The route shapes are preserved verbatim so the compose surface and every
/// existing client keep working — this removal changes WHERE the record lives,
/// never WHETHER it is written. The gateway authorizes the USER (jeeber/admin)
/// JWT at its own boundary before any of these are reached.</para>
/// </summary>
public interface ICodSettlementLedger
{
    /// <summary>POST /api/v1/payments/cod/record — records the COD intent for a settled delivery.</summary>
    Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct);

    /// <summary>GET /api/v1/payments/cod_jeeb/by-delivery/{deliveryId} — reads the COD record.</summary>
    Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct);

    /// <summary>POST /admin/v1/settlements/{batchId}/mark-paid — bank-confirmation, fronted by the gateway admin gate.</summary>
    Task<CodLedgerResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct);
}

/// <summary>
/// The ledger's HTTP-shaped outcome. The compose controller re-emits the status
/// code and JSON body unchanged so the wire contract published to clients is
/// never reshaped by the storage change.
///
/// <para><see cref="Available"/> exists only as a defensive guard for the
/// controller's 502 branch. With the in-process ledger it is always true — there
/// is no dial that can fail. It is retained (rather than removed along with the
/// HTTP client) so the controller keeps a total, non-throwing mapping if the
/// ledger is ever swapped for a durable store that can be unavailable.</para>
/// </summary>
public sealed record CodLedgerResult(bool Available, int StatusCode, string? Body, string ContentType);

/// <summary>
/// COD record request body. The gateway forwards the values the settlement row
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
