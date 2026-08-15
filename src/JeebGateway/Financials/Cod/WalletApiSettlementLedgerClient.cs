using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials.Cod;

// gwdbx W2-05 — posts one settlement to wallet-service POST /v1/holders/{id}/earnings (W2-01 #62).
// Idempotent: wallet upserts on (holderId, transactionId) and transactionId = settlement id.
public sealed class WalletApiSettlementLedgerClient
{
    public const string HttpClientName = "cod-wallet-mirror";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _clients;
    private readonly ILogger<WalletApiSettlementLedgerClient> _log;

    public WalletApiSettlementLedgerClient(
        IHttpClientFactory clients, ILogger<WalletApiSettlementLedgerClient> log)
    {
        _clients = clients;
        _log = log;
    }

    // Returns the wallet earning id to stamp into settlements.wallet_tx_id; null = permanently
    // unmappable row (non-GUID jeeber id), logged + skipped. Transport/5xx throw so sweeps retry.
    public async Task<string?> MirrorAsync(Settlement row, CancellationToken ct)
    {
        if (!Guid.TryParse(row.JeeberId, out var holderId))
        {
            _log.LogWarning(
                "cod wallet mirror SKIP settlement {SettlementId}: jeeberId {JeeberId} is not a "
                + "GUID wallet holder id; row left unstamped.", row.Id, row.JeeberId);
            return null;
        }

        // BR-16: commission is the STORED figure sent verbatim with its stored rate snapshot —
        // wallet persists it un-recomputed, so wallet net = gross - commission matches the gateway.
        var body = new RecordHolderEarningWire
        {
            TransactionId = row.Id,
            DeliveryId = Guid.TryParse(row.DeliveryId, out var deliveryId) ? deliveryId : null,
            TierName = string.IsNullOrWhiteSpace(row.TierId) ? null : row.TierId,
            Type = "delivery",
            Gross = row.GoodsCost,
            CommissionPercentage = row.CommissionRate,
            Commission = row.Commission,
            GoodsCost = row.GoodsCost,
            Insurance = row.Insurance,
            PaymentMethod = row.PaymentMethod,
            MinimumFeeApplied = row.MinimumFeeApplied,
            Currency = row.Currency,
            DeliveredAt = row.SettledAt.UtcDateTime,
        };

        var client = _clients.CreateClient(HttpClientName);
        using var response = await client.PostAsJsonAsync(
            $"v1/holders/{holderId}/earnings", body, Json, ct);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<HolderEarningWire>(Json, ct);
        if (dto is null || dto.EarningId == Guid.Empty)
        {
            throw new InvalidOperationException(
                $"wallet-service returned no earningId for settlement {row.Id}");
        }

        return dto.EarningId.ToString();
    }

    // Wire copy of wallet-service RecordHolderEarningRequest (holder-generic, G-28-clean).
    internal sealed class RecordHolderEarningWire
    {
        public required string TransactionId { get; init; }
        public Guid? DeliveryId { get; init; }
        public string? TierName { get; init; }
        public required string Type { get; init; }
        public decimal Gross { get; init; }
        public decimal? CommissionPercentage { get; init; }
        public decimal? Commission { get; init; }
        public decimal? GoodsCost { get; init; }
        public decimal? Insurance { get; init; }
        public string? PaymentMethod { get; init; }
        public bool? MinimumFeeApplied { get; init; }
        public required string Currency { get; init; }
        public DateTime? DeliveredAt { get; init; }
    }

    internal sealed class HolderEarningWire
    {
        public Guid EarningId { get; init; }
    }
}
