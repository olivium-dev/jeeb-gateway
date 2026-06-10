using System.Globalization;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// UPG-backed <see cref="ISettlementLedgerClient"/> (JEB-1484, GR3). Posts the
/// gateway-computed cash settlement to the Unified Payment Gateway's GENERIC
/// external-settlement endpoint via <see cref="IUpgSettlementClient"/>, so the
/// payments path runs THROUGH UPG instead of an in-process ledger.
///
/// The Jeeb fee policy is computed upstream of this client (in
/// <see cref="SettlementService"/> via <see cref="CommissionCalculator"/>); this
/// adapter only MAPS the already-computed amounts onto the product-agnostic
/// UPG primitive — it performs no settlement math:
///
///   source       = "jeeb.cod"           (the gateway-owned settlement channel)
///   externalRef  = deliveryId           (natural idempotency key)
///   payeeRef     = jeeberId
///   grossAmount  = total cash collected
///   feeAmount    = commission + insurance (platform fee)
///   netAmount    = goods cost (net to the payee)
///
/// Jeeb-specific identifiers (client id, tier, payment method) ride in
/// <c>metadata</c>, opaque to UPG. Registered behind
/// <c>FeatureFlags:UseUpstream:Payments</c> in Program.cs; when the flag is off
/// the gateway keeps the in-process <see cref="InMemorySettlementLedgerClient"/>.
/// </summary>
public sealed class UpgSettlementLedgerClient : ISettlementLedgerClient
{
    public const string Source = "jeeb.cod";

    private readonly IUpgSettlementClient _upg;
    private readonly TimeProvider _clock;
    private readonly ILogger<UpgSettlementLedgerClient> _log;

    public UpgSettlementLedgerClient(
        IUpgSettlementClient upg,
        TimeProvider clock,
        ILogger<UpgSettlementLedgerClient> log)
    {
        _upg = upg;
        _clock = clock;
        _log = log;
    }

    public async Task<LedgerEntryResponse> PostLedgerEntryAsync(LedgerEntryRequest request, CancellationToken ct)
    {
        var fee = request.Commission + request.Insurance;

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = request.ClientId,
            ["payment_method"] = request.PaymentMethod,
            ["entry_type"] = request.EntryType,
            ["commission"] = request.Commission.ToString("0.####", CultureInfo.InvariantCulture),
            ["insurance"] = request.Insurance.ToString("0.####", CultureInfo.InvariantCulture),
            ["gateway_settlement_id"] = request.IdempotencyKey,
        };

        var response = await _upg.RecordSettlementAsync(
            new UpgSettlementRequest
            {
                Source = Source,
                ExternalRef = request.DeliveryId,
                PayeeRef = request.JeeberId,
                GrossAmount = request.Total,
                FeeAmount = fee,
                NetAmount = request.GoodsCost,
                Currency = request.Currency,
                Metadata = metadata,
            },
            ct).ConfigureAwait(false);

        _log.LogInformation(
            "Posted cash settlement to UPG: deliveryId={DeliveryId} upgSettlementId={SettlementId}",
            request.DeliveryId, response.SettlementId);

        return new LedgerEntryResponse
        {
            LedgerEntryId = string.IsNullOrEmpty(response.SettlementId)
                ? request.IdempotencyKey
                : response.SettlementId,
            PostedAt = _clock.GetUtcNow(),
        };
    }
}
