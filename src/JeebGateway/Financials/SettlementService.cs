using JeebGateway.Observability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
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
    private readonly IDeliveryServiceClient _deliveryClient;
    private readonly IEarningsCacheInvalidator _earningsCache;
    private readonly TimeProvider _clock;
    private readonly ILogger<SettlementService> _log;

    public SettlementService(
        ISettlementStore store,
        IRequestsStore requests,
        ISettlementLedgerClient wallet,
        IDeliveryServiceClient deliveryClient,
        IEarningsCacheInvalidator earningsCache,
        TimeProvider clock,
        ILogger<SettlementService> log)
    {
        _store = store;
        _requests = requests;
        _wallet = wallet;
        _deliveryClient = deliveryClient;
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
        var settlement = BuildSettlement(
            delivery.Id, delivery.ClientId, delivery.JeeberId!, delivery.TierId,
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
        // The volatile in-memory request projection is the FAST path, not the source
        // of truth. A gateway restart mid-delivery wipes the row (and a multi-replica
        // deploy leaves a replica that never handled the completion holding a stale
        // pre-Done status), so neither the delivered-decision NOR the amount may key
        // off it alone — that is JEBV4-306 (a truly-Done COD delivery settling
        // NotDelivered/$0). Below, the delivered-decision derives from the CANONICAL
        // delivery-service state and the amount from a DURABLE store when the in-memory
        // row cannot answer.
        var delivery = await _requests.GetAsync(deliveryId, ct);

        // Exactly-once: an already-settled row means the jeeber is already credited —
        // never re-post the ledger, never double-credit. Resolved FIRST so it also
        // short-circuits before any canonical read-through. A pending-settlement
        // placeholder (State == PendingSettlement) does NOT short-circuit — it is the
        // durable COD snapshot we finish crediting below.
        var existing = await _store.GetByDeliveryAsync(deliveryId, ct);
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
        // in-memory row, else the canonical row, else the durable settlement snapshot.
        var jeeberId = FirstNonEmpty(delivery?.JeeberId, canonical?.JeeberId, existing?.JeeberId);
        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return new SettlementResult(SettlementOutcome.NotAuthorized, null,
                "No assigned jeeber to credit on completion.");
        }

        var clientId = FirstNonEmpty(delivery?.ClientId, canonical?.ClientId, existing?.ClientId) ?? string.Empty;
        var tierId = FirstNonEmpty(delivery?.TierId, canonical?.TierId, NullIfEmpty(existing?.TierId));
        var tier = CommissionCalculator.ResolveTier(tierId);

        // Q-011 / BR-16: the COD amount is SERVER-AUTHORITATIVE — the live row's
        // accepted-offer fee when present, else the DURABLE pending-settlement
        // snapshot's recorded goods cost (stamped at the AtDoor checkpoint via
        // TrySnapshotPendingCodAsync, which survives a restart). NEVER a caller body.
        var codAmount = (delivery?.AcceptedFee ?? 0m) > 0m
            ? delivery!.AcceptedFee!.Value
            : (existing?.GoodsCost ?? 0m);

        if (codAmount <= 0m)
        {
            // No authoritative amount anywhere: enqueue the pending-settlement
            // placeholder (goodsCost=0, NO ledger post) so the COD record/batch and a
            // later manual settle can still complete against a real row instead of
            // 404ing — never credit a bogus amount.
            if (existing is not null)
            {
                return new SettlementResult(SettlementOutcome.AlreadySettled, existing,
                    "pending intent already open; no server-authoritative amount yet");
            }

            var pending = BuildSettlement(
                deliveryId, clientId, jeeberId, tierId, existingId: null,
                CommissionCalculator.Calculate(0m, tier),
                PaymentMethodCash, SettlementState.PendingSettlement);
            var (pendingRow, pendingInserted) = await _store.TryInsertAsync(pending, ct);
            return new SettlementResult(
                pendingInserted ? SettlementOutcome.Settled : SettlementOutcome.AlreadySettled,
                pendingRow, "enqueued pending intent; no server-authoritative amount yet");
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
    /// <para>Called best-effort from the AtDoor checkpoints (OTP issue + the canonical
    /// PATCH → AtDoor), where the accepted-offer fee is already stamped on the live row.
    /// A pending placeholder is MONEY-SAFE: it carries no ledger post and is positively
    /// excluded from the weekly batch and the earnings/reconciler queries (they require
    /// state IN (settled, receipt_generated)). It is finished into a real settled row by
    /// <see cref="SettleOnCompletionAsync"/> via <c>ReplacePendingAsync</c> — exactly-once.</para>
    ///
    /// <para>Idempotent + degrade-don't-fail: a no-op when there is no live row, no
    /// assigned jeeber, no positive fee, or a settlement row already exists; returns
    /// <c>false</c> (never throws to the caller path) so it can never turn an AtDoor
    /// transition into a 5xx.</para>
    /// </summary>
    public async Task<bool> TrySnapshotPendingCodAsync(string deliveryId, CancellationToken ct)
    {
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null || string.IsNullOrWhiteSpace(delivery.JeeberId))
        {
            return false;
        }

        var fee = delivery.AcceptedFee ?? 0m;
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
            delivery.Id, delivery.ClientId, delivery.JeeberId!, delivery.TierId,
            existingId: null, breakdown, PaymentMethodCash, SettlementState.PendingSettlement);

        var (_, inserted) = await _store.TryInsertAsync(pending, ct);
        return inserted;
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

        // JEBV4-302: a fresh settled row just became visible to the earnings projection
        // (CodState=recorded ∈ EarningsStates). Evict this jeeber's cached earnings
        // windows so the /v1/jeebers/me/earnings 5-min cache cannot keep serving the
        // pre-settlement 0 for the rest of the TTL. Keyed off the settlement store row,
        // not the wallet ledger post, so the eviction stands even if the (best-effort)
        // ledger post below fails and is later replayed by the reconciler.
        _earningsCache.Invalidate(row.JeeberId);

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
            // JEBV4-47 (M3/R7): the UPG generic-settlement ledger post is best-effort
            // at the gateway boundary — the settlement row is the gateway-side system
            // of record and the ledger client is idempotent on the settlement id
            // (IdempotencyKey = row.Id). The row is left with ledger_entry_id NULL and
            // the SettlementLedgerReconciler (BackgroundService) replays the post on its
            // next tick via ISettlementStore.ListUnpostedLedgerAsync, so the gateway
            // settlement rows and the UPG ledger reconverge automatically. The failure
            // is counted so the (transient) divergence is observable, not silent.
            BusinessOutcomeTelemetry.SettlementLedgerPostFailures.Add(1);
            _log.LogWarning(ex,
                "Settlement ledger post failed for settlement {SettlementId} (delivery {DeliveryId}); "
                + "row persisted with ledger_entry_id NULL, SettlementLedgerReconciler will replay.",
                row.Id, row.DeliveryId);
        }

        return new SettlementResult(SettlementOutcome.Settled, row, null);
    }
}
