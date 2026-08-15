using System.Globalization;
using System.Text.Json;

namespace JeebGateway.Financials.Cod;

/// <summary>
/// COD recording through wallet-service. Unsupported legacy reads and batch
/// mutations fail closed; the gateway never creates a replacement ledger.
/// </summary>
public sealed class WalletCodSettlementLedger : ICodSettlementLedger
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ISettlementLedgerClient _wallet;

    public WalletCodSettlementLedger(ISettlementLedgerClient wallet) => _wallet = wallet;

    public async Task<CodLedgerResult> RecordCodAsync(CodRecordRequest request, CancellationToken ct)
    {
        var clientId = request.Metadata is not null
            && request.Metadata.TryGetValue("clientId", out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : "owner-unavailable";
        var entry = await _wallet.PostLedgerEntryAsync(new LedgerEntryRequest
        {
            IdempotencyKey = "cod:" + request.DeliveryId,
            DeliveryId = request.DeliveryId,
            JeeberId = request.JeeberId,
            ClientId = clientId,
            EntryType = "cod_settlement",
            GoodsCost = request.GrossAmount,
            Commission = request.CommissionAmount,
            Insurance = 0m,
            Total = request.CommissionAmount,
            Currency = request.Currency,
            PaymentMethod = "cash",
        }, ct);
        var body = JsonSerializer.Serialize(new
        {
            delivery_id = request.DeliveryId,
            jeeber_id = request.JeeberId,
            gross_amount = request.GrossAmount.ToString(CultureInfo.InvariantCulture),
            commission_amount = request.CommissionAmount.ToString(CultureInfo.InvariantCulture),
            currency = request.Currency,
            payment_method = "cash",
            status = "recorded",
            wallet_transaction_id = entry.LedgerEntryId,
        }, Json);
        return new CodLedgerResult(true, StatusCodes.Status201Created, body, "application/json");
    }

    public Task<CodLedgerResult> GetCodByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        Unavailable();

    public Task<CodLedgerResult> MarkBatchPaidAsync(
        string batchId, string paidByAdminId, CancellationToken ct) => Unavailable();

    private static Task<CodLedgerResult> Unavailable() => Task.FromResult(
        new CodLedgerResult(false, StatusCodes.Status503ServiceUnavailable, null, "application/problem+json"));
}
