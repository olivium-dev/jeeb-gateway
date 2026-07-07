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
    public const string CurrencyUsd = "USD";
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

        // Settle-ability keys off the CANONICAL handover-complete state, NOT the
        // legacy literals. OTP handover (S09) advances a real delivery to the
        // canonical `Done` token (DeliveryStatusAlias §3: "delivered => Done;
        // settlement keys off Done"); the legacy `delivered`/`rated` aliases also
        // resolve to `Done`. Gating on the legacy literals alone 409'd every real
        // Done delivery (S10 keystone). Dual-read via DeliveryStatusAlias so all
        // three spellings (Done / delivered / rated) settle, and nothing else does.
        if (!string.Equals(
                DeliveryStatusAlias.ToCanonical(delivery.Status),
                CanonicalDeliveryStatus.Done,
                StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.NotDelivered, null,
                $"Delivery is in '{delivery.Status}'; settlement requires the handover-complete state '{CanonicalDeliveryStatus.Done}'.");
        }

        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null
            && !string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            // Idempotent re-submission with real data: the original numbers stand. We do
            // not re-post the ledger entry — the wallet client itself is
            // idempotent on the settlement id, but skipping the call
            // keeps the settled-at timestamp stable as well.
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        // If there is an existing COD intent row (created by OTP verify, goodsCost=0),
        // we skip creating a new row and fall through to create/update with real amounts.
        // The TryInsertAsync will return the existing row if deliveryId conflicts.

        var tier = CommissionCalculator.ResolveTier(delivery.TierId);

        // Q-011 / BR-16: manual settle must use the same server-authoritative
        // accepted-offer amount as completion settlement. The body value is
        // client-supplied and must never choose the commission base.
        var codAmount = delivery.AcceptedFee ?? 0m;
        if (codAmount <= 0m)
        {
            return new SettlementResult(SettlementOutcome.InvalidAmount, null,
                "No server-authoritative accepted fee is available for this delivery.");
        }

        var breakdown = CommissionCalculator.Calculate(codAmount, tier);
        var settlement = BuildSettlement(delivery, existing?.Id, breakdown, paymentMethod, SettlementState.Settled);

        return await PersistAndCreditAsync(settlement, ct);
    }

    /// <summary>
    /// JEB (jeeber-earnings-on-complete): SERVER-DRIVEN settlement fired the moment
    /// the handover terminates (OTP verify → Done, or the customer's PATCH → Done),
    /// so the assigned jeeber is CREDITED on completion without any manual
    /// "record cash" step (none exists in the apps). Distinct from
    /// <see cref="SettleAsync"/>:
    /// <list type="bullet">
    ///   <item>NOT caller-authenticated — the SYSTEM settles on the jeeber's behalf
    ///         (the OTP-verify caller is the jeeber; the customer PATCH caller is the
    ///         client — neither supplies the amount).</item>
    ///   <item>BR-16: the COD amount is SERVER-AUTHORITATIVE — sourced from the
    ///         delivery row's agreed fee (<see cref="DeliveryRequest.AcceptedFee"/>,
    ///         stamped at accept from the accepted offer), NEVER a client body.</item>
    /// </list>
    /// Exactly-once credit: an already-settled row short-circuits (no second ledger
    /// post), and the wallet ledger post is itself idempotent on the settlement id,
    /// so firing on BOTH completion legs (verify + PATCH) credits exactly once.
    /// A missing/≤0 authoritative amount (older rows with no accepted-offer snapshot)
    /// enqueues the pending-settlement placeholder instead of crediting a bogus
    /// minimum-fee amount, keeping the COD-record + manual-settle window open.
    /// </summary>
    public async Task<SettlementResult> SettleOnCompletionAsync(string deliveryId, CancellationToken ct)
    {
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return new SettlementResult(SettlementOutcome.DeliveryNotFound, null, null);
        }

        if (string.IsNullOrWhiteSpace(delivery.JeeberId))
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "No assigned jeeber to credit on completion.");
        }

        // Settle-ability keys off the CANONICAL handover-complete state (Done, or the
        // legacy delivered/rated aliases) — mirrors SettleAsync's gate.
        if (!string.Equals(
                DeliveryStatusAlias.ToCanonical(delivery.Status),
                CanonicalDeliveryStatus.Done,
                StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.NotDelivered, null,
                $"Delivery is in '{delivery.Status}'; completion settlement requires the handover-complete state '{CanonicalDeliveryStatus.Done}'.");
        }

        // Exactly-once: an already-settled row means the jeeber is already credited —
        // never re-post the ledger, never double-credit.
        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null
            && !string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        var tier = CommissionCalculator.ResolveTier(delivery.TierId);

        // Q-011 / BR-16: server-authoritative amount from the delivery row (the
        // accepted offer fee the client owes the jeeber), NEVER a caller body.
        var codAmount = delivery.AcceptedFee ?? 0m;
        if (codAmount <= 0m)
        {
            // No authoritative amount yet: enqueue the pending-settlement placeholder
            // (goodsCost=0, NO ledger post) so the COD record/batch and a later manual
            // settle can still complete against a real row instead of 404ing.
            if (existing is not null)
            {
                return new SettlementResult(SettlementOutcome.AlreadySettled, existing,
                    "pending intent already open; no server-authoritative amount yet");
            }

            var pending = BuildSettlement(
                delivery, existingId: null,
                CommissionCalculator.Calculate(0m, tier),
                PaymentMethodCash, SettlementState.PendingSettlement);
            var (pendingRow, pendingInserted) = await _store.TryInsertAsync(pending, ct);
            return new SettlementResult(
                pendingInserted ? SettlementOutcome.Settled : SettlementOutcome.AlreadySettled,
                pendingRow, "enqueued pending intent; no server-authoritative amount yet");
        }

        var breakdown = CommissionCalculator.Calculate(codAmount, tier);
        var settlement = BuildSettlement(delivery, existing?.Id, breakdown, PaymentMethodCash, SettlementState.Settled);
        return await PersistAndCreditAsync(settlement, ct);
    }

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        _store.GetByDeliveryAsync(deliveryId, ct);

    /// <summary>
    /// Projects a delivery + a computed fee breakdown into a settlement row. Shared
    /// by the caller-authenticated <see cref="SettleAsync"/> and the server-driven
    /// <see cref="SettleOnCompletionAsync"/> so both produce a byte-identical row.
    /// </summary>
    private Settlement BuildSettlement(
        DeliveryRequest delivery,
        string? existingId,
        CommissionBreakdown breakdown,
        string paymentMethod,
        string state)
        => new()
        {
            Id = existingId ?? Guid.NewGuid().ToString(),
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
            Currency = CurrencyUsd,
            PaymentMethod = paymentMethod,
            State = state,
            CodState = CodSettlementState.Recorded,
            SettledAt = _clock.GetUtcNow(),
        };

    /// <summary>
    /// Persists a settled row (replacing an open pending-settlement placeholder when
    /// present, else inserting) and posts the best-effort wallet <c>cash_settlement</c>
    /// ledger entry that CREDITS the jeeber. Idempotent: the ledger post keys off the
    /// settlement id, and an insert conflict returns <see cref="SettlementOutcome.AlreadySettled"/>
    /// without a second post. Extracted verbatim from the original SettleAsync body.
    /// </summary>
    private async Task<SettlementResult> PersistAndCreditAsync(Settlement settlement, CancellationToken ct)
    {
        // FT-07: if a pending-settlement placeholder was created at OTP-verify time,
        // replace it atomically instead of inserting a duplicate. Falls through to
        // TryInsertAsync when no pending row exists (first-time settle path).
        bool inserted;
        Settlement row;
        var replaced = await _store.ReplacePendingAsync(settlement.DeliveryId, settlement, ct);
        if (replaced)
        {
            row = settlement;
            inserted = true;
        }
        else
        {
            (row, inserted) = await _store.TryInsertAsync(settlement, ct);
        }

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
}
