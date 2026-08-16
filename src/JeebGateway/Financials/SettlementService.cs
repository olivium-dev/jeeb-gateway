using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials;

/// <summary>
/// Default <see cref="ISettlementService"/> implementation. gwdbx W2-R11: the settlement rows,
/// the commission arithmetic and the ledger belong to settlement-service; this class keeps only
/// the gateway-side orchestration (delivery resolution, authorization, settle-ability).
/// </summary>
public sealed class SettlementService : ISettlementService
{
    public const string CurrencyUsd = "USD";
    public const string PaymentMethodCash = "cash";

    private readonly ISettlementServiceClient _settlements;
    private readonly IRequestsStore _requests;
    private readonly IDeliveryServiceClient _deliveryClient;
    private readonly IEarningsCacheInvalidator _earningsCache;
    private readonly ICommissionCollector _commission;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        ISettlementServiceClient settlements,
        IRequestsStore requests,
        IDeliveryServiceClient deliveryClient,
        IEarningsCacheInvalidator earningsCache,
        ICommissionCollector commission,
        ILogger<SettlementService> log)
    {
        _settlements = settlements;
        _requests = requests;
        _deliveryClient = deliveryClient;
        _earningsCache = earningsCache;
        _commission = commission;
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

        var existing = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null
            && !string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            // Idempotent re-submission with real data: the original numbers stand. Settle is
            // idempotent on the delivery id upstream, but skipping the call keeps settled_at stable.
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        // Q-011 / BR-16: manual settle must use the same server-authoritative
        // accepted-offer amount as completion settlement. The body value is
        // client-supplied and must never choose the commission base.
        var codAmount = delivery.AcceptedFee ?? 0m;
        if (codAmount <= 0m)
        {
            return new SettlementResult(SettlementOutcome.InvalidAmount, null,
                "No server-authoritative accepted fee is available for this delivery.");
        }

        return await SettleUpstreamAsync(new SettlementSettleCommand(
            deliveryId, delivery.JeeberId!, delivery.ClientId, delivery.TierId,
            codAmount, paymentMethod), ct);
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
    /// Exactly-once credit: an already-settled row short-circuits, and settlement-service
    /// is idempotent on the delivery id, so firing on BOTH completion legs (verify + PATCH)
    /// credits exactly once. A missing/≤0 authoritative amount (older rows with no
    /// accepted-offer snapshot) records the amount-less pending intent instead of crediting
    /// a bogus minimum-fee amount, keeping the COD-record + manual-settle window open.
    /// </summary>
    public async Task<SettlementResult> SettleOnCompletionAsync(string deliveryId, CancellationToken ct)
    {
        // The volatile in-memory request projection is the FAST path, not the source
        // of truth. A gateway restart mid-delivery wipes the row (and a multi-replica
        // deploy leaves a replica that never handled the completion holding a stale
        // pre-Done status), so neither the delivered-decision NOR the amount may key
        // off it alone — that is JEBV4-306 (a truly-Done COD delivery settling
        // NotDelivered/$0). Below, the delivered-decision derives from the CANONICAL
        // delivery-service state and the amount from settlement-service when the
        // in-memory row cannot answer.
        var delivery = await _requests.GetAsync(deliveryId, ct);

        // Exactly-once: an already-settled row means the jeeber is already credited —
        // never re-settle, never double-credit. Resolved FIRST so it also short-circuits
        // before any canonical read-through. A pending intent (State == PendingSettlement)
        // does NOT short-circuit — it is the open window we finish crediting below.
        var existing = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null
            && !string.Equals(existing.State, SettlementState.PendingSettlement, StringComparison.Ordinal))
        {
            return new SettlementResult(SettlementOutcome.AlreadySettled, existing, null);
        }

        // ---- Durable delivered-decision -------------------------------------
        // The in-memory row settles it when present AND already canonical-Done (the
        // fast, no-network path). Otherwise consult the canonical delivery-service row:
        // a restart-wiped (null) OR stale non-terminal in-memory row must NOT strand a
        // delivery delivery-service already advanced to Done.
        var delivered = IsCanonicalDone(delivery?.Status);
        DeliveryReadUpstream? canonical = null;
        if (!delivered)
        {
            canonical = await TryReadCanonicalAsync(deliveryId, ct);
            delivered = IsCanonicalDone(canonical?.Status);
        }

        if (!delivered)
        {
            var observed = delivery?.Status ?? canonical?.Status ?? "(unknown)";
            return new SettlementResult(SettlementOutcome.NotDelivered, null,
                $"Delivery is in '{observed}'; completion settlement requires the handover-complete state '{CanonicalDeliveryStatus.Done}'.");
        }

        // ---- Durable jeeber + amount ----------------------------------------
        // Identity fields come from the freshest available durable source: the live
        // in-memory row, else the canonical row, else the upstream settlement row.
        var jeeberId = FirstNonEmpty(delivery?.JeeberId, canonical?.JeeberId, existing?.JeeberId);
        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "No assigned jeeber to credit on completion.");
        }

        var clientId = FirstNonEmpty(delivery?.ClientId, canonical?.ClientId, existing?.ClientId) ?? string.Empty;
        var tierId = FirstNonEmpty(delivery?.TierId, canonical?.TierId, NullIfEmpty(existing?.TierId));

        // Q-011 / BR-16: the COD amount is SERVER-AUTHORITATIVE — the live row's
        // accepted-offer fee. NEVER a caller body. gwdbx W2-R11: settlement-service stores
        // money as NULL on a pending intent, so a pending row carries no amount to recover.
        var codAmount = delivery?.AcceptedFee ?? 0m;

        if (codAmount <= 0m)
        {
            // No authoritative amount anywhere: record the amount-less intent so the COD record
            // and a later manual settle land on a real row instead of 404ing — never credit $0.
            if (existing is not null)
            {
                return new SettlementResult(SettlementOutcome.AlreadySettled, existing,
                    "pending intent already open; no server-authoritative amount yet");
            }

            var pending = await SettleUpstreamAsync(new SettlementSettleCommand(
                deliveryId, jeeberId!, clientId, tierId, null, PaymentMethodCash), ct);
            return pending with { Reason = "enqueued pending intent; no server-authoritative amount yet" };
        }

        return await SettleUpstreamAsync(new SettlementSettleCommand(
            deliveryId, jeeberId!, clientId, tierId, codAmount, PaymentMethodCash), ct);
    }

    /// <summary>
    /// JEBV4-306: records the settlement intent at the AtDoor checkpoint so the commission
    /// window is open before the handover completes. gwdbx W2-R11: the upstream intent is
    /// amount-less by contract (money is NULL, not 0), so it is a WINDOW marker, not a
    /// durable amount snapshot — the amount still comes from the live delivery row.
    ///
    /// <para>Idempotent + degrade-don't-fail: a no-op when there is no live row, no
    /// assigned jeeber, no positive fee, or a settlement row already exists; returns
    /// <c>false</c> rather than throwing so it can never turn an AtDoor transition into a 5xx.</para>
    /// </summary>
    public async Task<bool> TrySnapshotPendingCodAsync(string deliveryId, CancellationToken ct)
    {
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null || string.IsNullOrWhiteSpace(delivery.JeeberId))
        {
            return false;
        }

        if ((delivery.AcceptedFee ?? 0m) <= 0m)
        {
            return false;
        }

        // Don't clobber an existing row (intent already open, or already settled) — settle is
        // idempotent on the delivery id, but short-circuit to save the round trip.
        var existing = await _settlements.GetByDeliveryAsync(deliveryId, ct);
        if (existing is not null)
        {
            return false;
        }

        var result = await _settlements.SettleAsync(new SettlementSettleCommand(
            deliveryId, delivery.JeeberId!, delivery.ClientId, delivery.TierId,
            null, PaymentMethodCash), ct);
        return result.Created;
    }

    /// <summary>Reads the canonical delivery-service row, degrading a 404/transport fault
    /// to <see langword="null"/> so the completion path never turns a real Done into a 5xx.</summary>
    private async Task<DeliveryReadUpstream?> TryReadCanonicalAsync(string deliveryId, CancellationToken ct)
    {
        try
        {
            return await _deliveryClient.GetCanonicalDeliveryAsync(deliveryId, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "settlement.on_complete canonical read-through failed for delivery {DeliveryId}; "
                + "falling back to the in-memory/durable projection.", deliveryId);
            return null;
        }
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
        _settlements.GetByDeliveryAsync(deliveryId, ct);

    /// <summary>
    /// Posts the settle to settlement-service, which owns the commission arithmetic and the
    /// ledger, and evicts this jeeber's cached earnings windows on a fresh row (JEBV4-302) so
    /// the 5-min cache cannot keep serving the pre-settlement 0 for the rest of the TTL.
    /// </summary>
    private async Task<SettlementResult> SettleUpstreamAsync(
        SettlementSettleCommand command, CancellationToken ct)
    {
        var result = await _settlements.SettleAsync(command, ct);
        if (result.HolderExcluded)
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "Holder id is formally excluded from settlement (A21 §6 / D7).");
        }

        if (result.Row is null)
        {
            return new SettlementResult(SettlementOutcome.DeliveryNotFound, null,
                "settlement-service returned no settlement row.");
        }

        if (result.Created)
        {
            _earningsCache.Invalidate(result.Row.JeeberId);

            // O1 (ADR-0011 + the accept amendment): the fee was taken at ACCEPT, so this moves no
            // money — it only links the row to that debit, and counts the rows that have none.
            await _commission.LinkSettlementAsync(result.Row, ct);

            return new SettlementResult(SettlementOutcome.Settled, result.Row, null);
        }

        return new SettlementResult(SettlementOutcome.AlreadySettled, result.Row, null);
    }
}
