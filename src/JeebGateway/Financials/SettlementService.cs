using JeebGateway.Requests;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// Default <see cref="ISettlementService"/> implementation. Wires the
/// settlement store, the request store (for delivery resolution + auth),
/// and the wallet-service client into the orchestration described in the
/// interface docs.
/// </summary>
public sealed class SettlementService : ISettlementService
{
    public const string CurrencyLbp = "LBP";
    public const string PaymentMethodCash = "cash";

    private readonly ISettlementStore _store;
    private readonly IRequestsStore _requests;
    private readonly ISettlementLedgerClient _wallet;
    private readonly TimeProvider _clock;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        ISettlementStore store,
        IRequestsStore requests,
        ISettlementLedgerClient wallet,
        TimeProvider clock,
        ILogger<SettlementService> log)
    {
        _store = store;
        _requests = requests;
        _wallet = wallet;
        _clock = clock;
        _log = log;
    }

    public async Task<SettlementResult> SettleAsync(
        string deliveryId,
        string callerUserId,
        bool callerIsJeeber,
        SettleDeliveryRequest body,
        CancellationToken ct)
    {
        if (body.GoodsCost < 0)
        {
            return new SettlementResult(SettlementOutcome.InvalidAmount, null,
                "goodsCost must be non-negative.");
        }

        var paymentMethod = string.IsNullOrWhiteSpace(body.PaymentMethod)
            ? PaymentMethodCash
            : body.PaymentMethod.Trim().ToLowerInvariant();
        if (paymentMethod != PaymentMethodCash)
        {
            return new SettlementResult(SettlementOutcome.InvalidPaymentMethod, null,
                "Only cash settlements are supported in MVP.");
        }

        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return new SettlementResult(SettlementOutcome.DeliveryNotFound, null, null);
        }

        if (!callerIsJeeber || !string.Equals(delivery.JeeberId, callerUserId, StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "Only the assigned Jeeber can settle this delivery.");
        }

        if (!string.Equals(delivery.Status, RequestStatus.Delivered, StringComparison.Ordinal)
            && !string.Equals(delivery.Status, RequestStatus.Rated, StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.NotDelivered, null,
                $"Delivery is in '{delivery.Status}'; settlement requires '{RequestStatus.Delivered}'.");
        }

        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null)
        {
            // Idempotent re-submission: the original numbers stand. We do
            // not re-post the ledger entry — the wallet client itself is
            // idempotent on the settlement id, but skipping the call
            // keeps the settled-at timestamp stable as well.
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        var tier = CommissionCalculator.ResolveTier(delivery.TierId);
        var breakdown = CommissionCalculator.Calculate(body.GoodsCost, tier);

        var settlement = new Settlement
        {
            Id = Guid.NewGuid().ToString(),
            DeliveryId = delivery.Id,
            ClientId = delivery.ClientId,
            JeeberId = delivery.JeeberId!,
            TierId = delivery.TierId ?? string.Empty,
            GoodsCost = breakdown.GoodsCost,
            CommissionTier = breakdown.Tier,
            CommissionRate = breakdown.CommissionRate,
            Commission = breakdown.Commission,
            Insurance = breakdown.Insurance,
            Total = breakdown.Total,
            MinimumFeeApplied = breakdown.MinimumFeeApplied,
            Currency = CurrencyLbp,
            PaymentMethod = paymentMethod,
            State = SettlementState.Settled,
            SettledAt = _clock.GetUtcNow(),
        };

        var (row, inserted) = await _store.TryInsertAsync(settlement, ct);
        if (!inserted)
        {
            return new SettlementResult(SettlementOutcome.AlreadySettled, row, null);
        }

        try
        {
            var ledger = await _wallet.PostLedgerEntryAsync(new LedgerEntryRequest
            {
                DeliveryId = row.DeliveryId,
                JeeberId = row.JeeberId,
                ClientId = row.ClientId,
                EntryType = "cash_settlement",
                GoodsCost = row.GoodsCost,
                Commission = row.Commission,
                Insurance = row.Insurance,
                Total = row.Total,
                Currency = row.Currency,
                PaymentMethod = row.PaymentMethod,
                IdempotencyKey = row.Id,
            }, ct);

            await _store.SetLedgerEntryAsync(row.Id, ledger.LedgerEntryId, ct);
            row.LedgerEntryId = ledger.LedgerEntryId;
        }
        catch (Exception ex)
        {
            // wallet-service is best-effort at the gateway boundary: the
            // settlement row is the system of record on the gateway side
            // and the wallet client is idempotent on the settlement id,
            // so the background ledger reconciler can replay the post.
            _log.LogWarning(ex,
                "Wallet ledger post failed for settlement {SettlementId} (delivery {DeliveryId}); row persisted, will replay.",
                row.Id, row.DeliveryId);
        }

        return new SettlementResult(SettlementOutcome.Settled, row, null);
    }

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        _store.GetByDeliveryAsync(deliveryId, ct);
}
