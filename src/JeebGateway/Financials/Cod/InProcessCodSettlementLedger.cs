using System.Text.Json;

namespace JeebGateway.Financials.Cod;

/// <summary>
/// The COD settlement ledger of record.
///
/// <para>OWNER RULING 2026-07-27 — "jeeb is only cash on delivery", no
/// unified_payment_gateway. Cash is collected hand-to-hand by the Jeeber and the
/// commission is computed by the gateway (CommissionCalculator /
/// SettlementService). There is no external settlement destination, so there is
/// nothing to dial: this in-process ledger IS the record, not a stand-in for
/// one.</para>
///
/// <para>This class replaces the former <c>InMemoryUnifiedPaymentCodClient</c>
/// dev/test fallback that sat beside an HTTP sibling
/// (<c>HttpUnifiedPaymentCodClient</c>, deleted) which dialed UPG whenever
/// <c>Services:UnifiedPayment:BaseUrl</c> was configured. The behaviour is
/// unchanged — production has always run this implementation, because the
/// BaseUrl was never set outside the removed config key — but it is no longer a
/// FALLBACK. It is the permanent implementation.</para>
///
/// <para>ASYMMETRY (deliberate — see <c>Services/Clients/CashOnDeliveryNoRefundClient</c>):
/// the dispute-refund client had to be made to FAIL LOUDLY, because it reported
/// SUCCESS for money that never moved. This ledger is the opposite case. It
/// records cash that was ALREADY collected in person; the recording is the whole
/// operation, and it tells the truth. A money path is only safe to serve
/// in-process when the in-process answer is the true one.</para>
///
/// <para>Idempotent on delivery id (a re-record of the same delivery overwrites
/// the identical row rather than minting a second one), matching the natural key
/// the settlement row already uses.</para>
/// </summary>
public sealed class InProcessCodSettlementLedger : ICodSettlementLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> _byDelivery = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _batchStatus = new(StringComparer.Ordinal);

    public Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct)
    {
        var batchId = $"batch-{request.JeeberId}";
        var record = new
        {
            delivery_id = request.DeliveryId,
            provider_id = request.JeeberId,
            jeeber_id = request.JeeberId,
            gross_amount = request.GrossAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            commission_amount = request.CommissionAmount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            currency = request.Currency,
            payment_method = "cash",
            status = "batched",
            batchId,
        };
        _byDelivery[request.DeliveryId] = record;
        _batchStatus.TryAdd(batchId, "ready_to_pay");
        return Json(StatusCodes.Status201Created, new { data = record });
    }

    public Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        if (_byDelivery.TryGetValue(deliveryId, out var record))
            return Json(StatusCodes.Status200OK, record);
        return Json(StatusCodes.Status404NotFound, new { error = "not_found" });
    }

    public Task<CodLedgerResult> MarkBatchPaidAsync(string batchId, string paidByAdminId, CancellationToken ct)
    {
        if (!_batchStatus.TryGetValue(batchId, out var status))
            return Json(StatusCodes.Status404NotFound, new { error = "not_found" });
        if (string.Equals(status, "paid", StringComparison.Ordinal))
            return Json(StatusCodes.Status409Conflict, new { error = "already_paid" });
        if (string.Equals(status, "cancelled", StringComparison.Ordinal))
            return Json(StatusCodes.Status422UnprocessableEntity, new { error = "terminal_non_payable" });

        _batchStatus[batchId] = "paid";
        return Json(StatusCodes.Status200OK, new
        {
            status = "paid",
            paidBy = paidByAdminId,
            paidAt = DateTimeOffset.UtcNow,
            batchId,
        });
    }

    private static Task<CodLedgerResult> Json(int status, object body) =>
        Task.FromResult(new CodLedgerResult(true, status,
            JsonSerializer.Serialize(body, JsonOptions), "application/json"));
}
