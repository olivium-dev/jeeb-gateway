using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using JeebGateway.Financials.Cod;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.IntegrationTests.Fakes;

/// <summary>
/// Explicit test-only COD owner double. The production gateway records COD in
/// wallet-service and never compiles or registers a process-local ledger.
/// </summary>
internal sealed class TestCodSettlementLedger : ICodSettlementLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, object> _byDelivery = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _batchStatus = new(StringComparer.Ordinal);

    public Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct)
    {
        var batchId = $"batch-{request.JeeberId}";
        var record = new
        {
            delivery_id = request.DeliveryId,
            provider_id = request.JeeberId,
            jeeber_id = request.JeeberId,
            gross_amount = request.GrossAmount.ToString(CultureInfo.InvariantCulture),
            commission_amount = request.CommissionAmount.ToString(CultureInfo.InvariantCulture),
            currency = request.Currency,
            payment_method = "cash",
            status = "batched",
            batchId,
        };
        _byDelivery[request.DeliveryId] = record;
        _batchStatus.TryAdd(batchId, "ready_to_pay");
        return Result(StatusCodes.Status201Created, new { data = record });
    }

    public Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        _byDelivery.TryGetValue(deliveryId, out var record)
            ? Result(StatusCodes.Status200OK, record)
            : Result(StatusCodes.Status404NotFound, new { error = "not_found" });

    public Task<CodLedgerResult> MarkBatchPaidAsync(
        string batchId,
        string paidByAdminId,
        CancellationToken ct)
    {
        if (!_batchStatus.TryGetValue(batchId, out var status))
            return Result(StatusCodes.Status404NotFound, new { error = "not_found" });
        if (string.Equals(status, "paid", StringComparison.Ordinal))
            return Result(StatusCodes.Status409Conflict, new { error = "already_paid" });

        _batchStatus[batchId] = "paid";
        return Result(StatusCodes.Status200OK, new
        {
            status = "paid",
            paidBy = paidByAdminId,
            batchId,
        });
    }

    private static Task<CodLedgerResult> Result(int status, object body) =>
        Task.FromResult(new CodLedgerResult(
            true,
            status,
            JsonSerializer.Serialize(body, Json),
            "application/json"));
}
