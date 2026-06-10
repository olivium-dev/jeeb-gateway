namespace JeebGateway.Financials.Cod;

/// <summary>
/// Thin typed client over unified_payment_gateway (UPG, port 10066) for the COD
/// settlement + admin-batch surface (S10 H3.3/H4/N10-N12).
///
/// <para>LAWS honored: payments are owned by UPG — the gateway NEVER touches a
/// provider directly. This client only RECORDS an intent / READS a record /
/// FRONTS the admin mark-paid action against UPG's live routes
/// (router.ex §COD + §admin_settlements). No money moves at the gateway; UPG is
/// the money system of record. NO inter-service coupling beyond the gateway BFF
/// composing UPG — the gateway authorizes the USER (jeeber/admin) JWT at its own
/// boundary, then dials UPG with UPG's own pipeline credentials
/// (X-Api-Key for the :api pipeline; admin bearer + X-Admin-Id for AdminAuthPlug).</para>
/// </summary>
public interface IUnifiedPaymentCodClient
{
    /// <summary>POST /api/v1/payments/cod/record — records the COD intent for a settled delivery.</summary>
    Task<UpgResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct);

    /// <summary>GET /api/v1/payments/cod/by-delivery/{deliveryId} — reads the COD record.</summary>
    Task<UpgResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct);

    /// <summary>POST /admin/v1/settlements/{batchId}/mark-paid — bank-confirmation, fronted by the gateway admin gate.</summary>
    Task<UpgResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct);
}

/// <summary>
/// Verbatim pass-through of UPG's HTTP outcome — the gateway re-emits the status
/// code and JSON body unchanged so it never reshapes (and never drifts from)
/// UPG's contract. <see cref="Reachable"/> is false only when the dial failed
/// (UPG unreachable / not configured) so the controller can surface a 502/503.
/// </summary>
public sealed record UpgResult(bool Reachable, int StatusCode, string? Body, string ContentType)
{
    public static UpgResult Unreachable() => new(false, 0, null, "application/json");
}

/// <summary>
/// COD record request body. Mirrors UPG CodSettlementService.record/1 accepted
/// keys (delivery_id natural idempotency key; provider/jeeber id aliases;
/// gross/commission; currency; metadata). The gateway forwards the values the
/// settlement row already computed — UPG copies them verbatim (BR-16).
/// </summary>
public sealed record CodRecordRequest(
    string DeliveryId,
    string JeeberId,
    decimal GrossAmount,
    decimal CommissionRate,
    decimal CommissionAmount,
    string Currency,
    IReadOnlyDictionary<string, string>? Metadata = null);
