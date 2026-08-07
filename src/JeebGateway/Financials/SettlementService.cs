using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// Default <see cref="ISettlementService"/> implementation. Wires the
/// settlement store, the request store (for delivery resolution + auth),
/// into the orchestration described in the interface docs. UPG owns all money
/// persistence, batching, idempotency, audit and outbox work.
/// </summary>
public sealed class SettlementService : ISettlementService
{
    public const string CurrencyUsd = "USD";
    public const string PaymentMethodCash = "cash";

    private readonly ISettlementStore _store;
    private readonly IDeliveryServiceClient _deliveryClient;
    private readonly IOfferServiceClient _offers;
    private readonly IEarningsCacheInvalidator _earningsCache;
    private readonly TimeProvider _clock;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        ISettlementStore store,
        IDeliveryServiceClient deliveryClient,
        IOfferServiceClient offers,
        IEarningsCacheInvalidator earningsCache,
        TimeProvider clock,
        ILogger<SettlementService> log)
    {
        _store = store;
        _deliveryClient = deliveryClient;
        _offers = offers;
        _earningsCache = earningsCache;
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

        DeliveryReadUpstream? delivery;
        try
        {
            delivery = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "settlement canonical delivery read failed for {DeliveryId}", deliveryId);
            return new SettlementResult(SettlementOutcome.DependencyUnavailable, null,
                "The delivery owner could not be consulted.");
        }
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
            // not re-submit the COD record — the durable owner is idempotent on
            // the settlement id, but skipping the call
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
        decimal? authoritativeFee;
        try
        {
            authoritativeFee = await ReadAcceptedOfferFeeAsync(delivery, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "settlement accepted-offer read failed for {DeliveryId}", deliveryId);
            return new SettlementResult(SettlementOutcome.DependencyUnavailable, null,
                "The offer owner could not be consulted.");
        }
        var codAmount = authoritativeFee ?? 0m;
        if (codAmount <= 0m)
        {
            return new SettlementResult(SettlementOutcome.InvalidAmount, null,
                "No server-authoritative accepted fee is available for this delivery.");
        }

        var breakdown = CommissionCalculator.Calculate(codAmount, tier);
        var settlement = BuildSettlement(
            delivery.DeliveryId, delivery.ClientId ?? string.Empty, delivery.JeeberId!, delivery.TierId,
            existing?.Id, breakdown, paymentMethod, SettlementState.Settled);

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
    ///   <item>BR-16: the COD amount is SERVER-AUTHORITATIVE — composed from
    ///         delivery-service identity plus offer-service's accepted winner,
    ///         NEVER a client body or gateway-local request projection.</item>
    /// </list>
    /// Exactly-once recording: an already-settled projection short-circuits, and
    /// unified-payment-gateway is idempotent on the settlement id, so firing on
    /// both completion legs (verify + PATCH) records COD exactly once.
    /// A missing/≤0 authoritative amount (older rows with no accepted-offer snapshot)
    /// enqueues the pending-settlement placeholder instead of crediting a bogus
    /// minimum-fee amount, keeping the COD-record + manual-settle window open.
    /// </summary>
    public async Task<SettlementResult> SettleOnCompletionAsync(string deliveryId, CancellationToken ct)
    {
        // Exactly-once: an already-settled owner projection means COD was recorded —
        // never resubmit it. Resolved FIRST so it also
        // short-circuits before any canonical read-through. A pending-settlement
        // placeholder (State == PendingSettlement) does NOT short-circuit — it is the
        // durable COD snapshot we finish crediting below.
        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null
            && !string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        DeliveryReadUpstream? canonical;
        try
        {
            canonical = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            _log.LogWarning(error, "settlement completion delivery read failed for {DeliveryId}", deliveryId);
            return new SettlementResult(SettlementOutcome.DependencyUnavailable, null,
                "The delivery owner could not be consulted.");
        }
        if (canonical is null)
            return new SettlementResult(SettlementOutcome.DeliveryNotFound, null, null);
        if (!IsCanonicalDone(canonical.Status))
        {
            return new SettlementResult(SettlementOutcome.NotDelivered, null,
                $"Delivery is in '{canonical.Status}'; completion settlement requires the handover-complete state '{CanonicalDeliveryStatus.Done}'.");
        }

        var jeeberId = FirstNonEmpty(canonical.JeeberId, existing?.JeeberId);
        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "No assigned jeeber to credit on completion.");
        }

        var clientId = FirstNonEmpty(canonical.ClientId, existing?.ClientId) ?? string.Empty;
        var tierId = FirstNonEmpty(canonical.TierId, NullIfEmpty(existing?.TierId));
        var tier = CommissionCalculator.ResolveTier(tierId);

        // A positive owner snapshot is authoritative after AtDoor. If it is absent,
        // strictly re-read the accepted offer; never consult gateway-local state.
        var codAmount = existing?.GoodsCost ?? 0m;
        if (codAmount <= 0m)
        {
            try
            {
                codAmount = await ReadAcceptedOfferFeeAsync(canonical, ct) ?? 0m;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                _log.LogWarning(error, "settlement completion offer read failed for {DeliveryId}", deliveryId);
                return new SettlementResult(SettlementOutcome.DependencyUnavailable, null,
                    "The offer owner could not be consulted.");
            }
        }

        if (codAmount <= 0m)
        {
            if (existing is not null)
            {
                return new SettlementResult(SettlementOutcome.AlreadySettled, existing,
                    "pending intent already open; no server-authoritative amount yet");
            }
            return new SettlementResult(SettlementOutcome.InvalidAmount, null,
                "no server-authoritative COD amount is available; no zero-value owner intent was written");
        }

        var breakdown = CommissionCalculator.Calculate(codAmount, tier);
        var settlement = BuildSettlement(
            deliveryId, clientId, jeeberId, tierId, existing?.Id, breakdown,
            PaymentMethodCash, SettlementState.Settled);
        return await PersistAndCreditAsync(settlement, ct);
    }

    /// <summary>
    /// JEBV4-306: durably snapshots the SERVER-AUTHORITATIVE COD amount into the
    /// settlement store as a <see cref="SettlementState.PendingSettlement"/> placeholder
    /// BEFORE the handover completes, so that if the gateway restarts (or a settling
    /// replica never held the in-memory row) the completion settlement can still recover
    /// the amount from a durable store rather than crediting $0.
    ///
    /// <para>Called as a hard precondition before the canonical PATCH → AtDoor,
    /// where the accepted-offer fee is stamped on the live row.
    /// A pending intent is money-safe: unified-payment-gateway excludes it from
    /// payable reconciliation until <see cref="SettleOnCompletionAsync"/> finalizes
    /// it exactly once.</para>
    ///
    /// <para>Returns <c>false</c> when the authoritative inputs are incomplete or a
    /// settlement row already exists. Owner transport/conflict failures propagate so
    /// the AtDoor caller can fail closed before committing the delivery transition.</para>
    /// </summary>
    public async Task<bool> TrySnapshotPendingCodAsync(string deliveryId, CancellationToken ct)
    {
        var delivery = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        if (delivery is null
            || string.IsNullOrWhiteSpace(delivery.ClientId)
            || string.IsNullOrWhiteSpace(delivery.JeeberId))
        {
            return false;
        }

        var fee = await ReadAcceptedOfferFeeAsync(delivery, ct) ?? 0m;
        if (fee <= 0m)
        {
            return false;
        }

        // Don't clobber an existing row (pending snapshot already taken, or already
        // settled) — TryInsertAsync is idempotent on delivery id, but short-circuit to
        // avoid the needless commission compute.
        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null)
        {
            return false;
        }

        var tier = CommissionCalculator.ResolveTier(delivery.TierId);
        var breakdown = CommissionCalculator.Calculate(fee, tier);
        var pending = BuildSettlement(
            delivery.DeliveryId, delivery.ClientId ?? string.Empty, delivery.JeeberId!, delivery.TierId,
            existingId: null, breakdown, PaymentMethodCash, SettlementState.PendingSettlement);

        var (_, inserted) = await _store.TryInsertAsync(pending, ct);
        return inserted;
    }

    public async Task<bool> IsAuthoritativeCodIntentAsync(
        string deliveryId,
        string? deliveryStatusBeforeTransition,
        CancellationToken ct)
    {
        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
        if (existing is null) return false;

        var delivery = await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        if (delivery is null
            || string.IsNullOrWhiteSpace(delivery.ClientId)
            || string.IsNullOrWhiteSpace(delivery.JeeberId)) return false;
        var fee = await ReadAcceptedOfferFeeAsync(delivery, ct);
        if (fee is null or <= 0m) return false;

        var identityMatches = string.Equals(existing.DeliveryId, deliveryId, StringComparison.Ordinal)
                              && string.Equals(existing.ClientId, delivery.ClientId, StringComparison.Ordinal)
                              && string.Equals(existing.JeeberId, delivery.JeeberId, StringComparison.Ordinal)
                              && string.Equals(existing.TierId, delivery.TierId ?? string.Empty, StringComparison.Ordinal)
                              && string.Equals(existing.Currency, CurrencyUsd, StringComparison.Ordinal)
                              && string.Equals(existing.PaymentMethod, PaymentMethodCash, StringComparison.Ordinal)
                              && decimal.Round(existing.GoodsCost, 2, MidpointRounding.AwayFromZero)
                              == decimal.Round(fee.Value, 2, MidpointRounding.AwayFromZero);
        if (!identityMatches) return false;

        var openIntent = string.Equals(
                             existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal)
                         && string.Equals(existing.CodState, CodSettlementState.Recorded, StringComparison.Ordinal);
        if (openIntent) return true;

        return string.Equals(
            DeliveryStatusAlias.ToCanonical(deliveryStatusBeforeTransition),
            CanonicalDeliveryStatus.AtDoor,
            StringComparison.Ordinal);
    }

    private async Task<decimal?> ReadAcceptedOfferFeeAsync(
        DeliveryReadUpstream delivery,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(delivery.ClientId)
            || string.IsNullOrWhiteSpace(delivery.JeeberId)) return null;

        var read = await _offers.ListForRequestStrictAsync(
            delivery.ClientId, delivery.DeliveryId, ct);
        if (read.Status != OfferRequestReadStatus.Ok)
            throw new InvalidOperationException($"Offer owner read failed with {read.Status}.");

        var accepted = read.Offers.Where(offer =>
                string.Equals(offer.RequestId, delivery.DeliveryId, StringComparison.Ordinal)
                && string.Equals(offer.JeeberId, delivery.JeeberId, StringComparison.Ordinal)
                && string.Equals(offer.Status, "accepted", StringComparison.OrdinalIgnoreCase)
                && offer.FeeCents > 0)
            .Take(2)
            .ToArray();
        if (accepted.Length == 0) return null;
        if (accepted.Length > 1)
            throw new InvalidOperationException("Offer owner returned multiple accepted winners.");
        return accepted[0].FeeCents / 100m;
    }

    private static bool IsCanonicalDone(string? status) =>
        string.Equals(DeliveryStatusAlias.ToCanonical(status), CanonicalDeliveryStatus.Done, StringComparison.Ordinal);

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct) =>
        _store.GetByDeliveryAsync(deliveryId, ct);

    /// <summary>
    /// Projects a delivery identity + a computed fee breakdown into a settlement row.
    /// Shared by the caller-authenticated <see cref="SettleAsync"/> and the server-driven
    /// <see cref="SettleOnCompletionAsync"/> so both produce a byte-identical row. Takes
    /// the identity fields as primitives (not a <see cref="DeliveryRequest"/>) so the
    /// completion path can build the row from a DURABLE source when the in-memory request
    /// projection has been wiped by a restart (JEBV4-306).
    /// </summary>
    private Settlement BuildSettlement(
        string deliveryId,
        string clientId,
        string jeeberId,
        string? tierId,
        string? existingId,
        CommissionBreakdown breakdown,
        string paymentMethod,
        string state)
        => new()
        {
            Id = existingId ?? Guid.NewGuid().ToString(),
            DeliveryId = deliveryId,
            ClientId = clientId,
            JeeberId = jeeberId,
            TierId = tierId ?? string.Empty,
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
    /// Finalizes a COD intent in unified-payment-gateway (replacing an open pending
    /// owner record when present). The owner keys idempotency on the settlement id;
    /// a replay returns <see cref="SettlementOutcome.AlreadySettled"/> without a
    /// duplicate durable record.
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

        // The owner transition is complete and visible to the earnings projection.
        _earningsCache.Invalidate(row.JeeberId);

        return new SettlementResult(SettlementOutcome.Settled, row, null);
    }
}
