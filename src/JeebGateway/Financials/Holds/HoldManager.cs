using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Financials.Holds;

/// <summary>Outcome of a hold placement; exactly one flag is ever set. The caller maps
/// Insufficient to 402 E1, Unavailable to 503 E6 and ExposureUnresolvable to 503 E5.</summary>
/// <remarks><c>TxId</c> names the leg THIS call placed (Guid.Empty when nothing was placed), so a
/// caller whose own transaction then fails can roll back its leg without touching the rest.</remarks>
public sealed record HoldPlacement(
    bool Placed, bool Insufficient, bool Unavailable, bool ExposureUnresolvable, Guid TxId)
{
    public static HoldPlacement Ok(Guid txId = default) => new(true, false, false, false, txId);

    public static HoldPlacement InsufficientFunds() => new(false, true, false, false, default);

    public static HoldPlacement WalletUnavailable() => new(false, false, true, false, default);

    public static HoldPlacement Unresolvable() => new(false, false, false, true, default);
}

/// <summary>DECISION-holds-mechanism Ops 1-4 — the whole hold surface: wallet-service HTTP plus the
/// durable intent record. Placement callers MUST hold the per-jeeber serializer lock (I5).</summary>
public interface IHoldManager
{
    /// <summary>Op 1 — place this offer's commission as a pending hold.</summary>
    Task<HoldPlacement> PlaceOnSubmitAsync(
        Guid jeeberGuid, string jeeberId, string offerId, string requestId,
        decimal thisOfferCommission, CancellationToken ct);

    /// <summary>Op 2 — top the hold set up to the raised fee's commission. A lower or equal
    /// total is a no-op success: over-hold is the safe direction.</summary>
    Task<HoldPlacement> RaiseDeltaAsync(
        Guid jeeberGuid, string jeeberId, string offerId, string requestId,
        decimal newFeeCommissionTotal, CancellationToken ct);

    /// <summary>Op 3 — abort every header under the offer's reference. Idempotent, and NEVER
    /// throws: a release failure must not block the user-facing transition.</summary>
    Task ReleaseForOfferAsync(string offerId, string reason, CancellationToken ct);

    /// <summary>Op 3 fan-out over a request's live offers (expiry, client cancel).</summary>
    Task ReleaseForRequestAsync(string requestId, string reason, CancellationToken ct);

    /// <summary>Op 3 fan-out over a jeeber's offers whose withdraw is CONFIRMED (auto-offline,
    /// unregister): terminal offers only, so an offer still live upstream keeps its collateral.</summary>
    Task ReleaseWithdrawnForJeeberAsync(string jeeberId, string reason, CancellationToken ct);

    /// <summary>Roll back ONE leg this caller just placed (an accept revalidation whose saga did
    /// not commit). Aborts that header alone, so a pre-existing partial hold set survives.</summary>
    Task RollbackLegAsync(string offerId, Guid txId, string reason, CancellationToken ct);

    /// <summary>Op 4 — capture-by-conversion step 1: drop the hold set so the existing
    /// collector's single debit can go through. Flag-ON path only.</summary>
    Task AbortHoldSetForCaptureAsync(string offerId, CancellationToken ct);
}

public sealed class HoldManager : IHoldManager
{
    /// <summary>Frozen wallet-service constants (DECISION, Naming). <c>hold</c> is deliberately
    /// distinct from the capture tag <c>platform-fee</c>.</summary>
    internal const string HoldTag = "hold";

    internal const string ExternalReferencePrefix = "jeeb:offer:";
    internal const string IdempotencyKeyPrefix = "jeeb:hold:";

    private readonly IWalletCommissionDebitClient _wallet;
    private readonly IHoldIntentStore _intents;
    private readonly IPendingOffersStore _offers;
    private readonly TimeProvider _time;
    private readonly ILogger<HoldManager> _log;

    public HoldManager(
        IWalletCommissionDebitClient wallet,
        IHoldIntentStore intents,
        IPendingOffersStore offers,
        TimeProvider time,
        ILogger<HoldManager> log)
    {
        _wallet = wallet;
        _intents = intents;
        _offers = offers;
        _time = time;
        _log = log;
    }

    internal static string ExternalReferenceFor(string offerId) => ExternalReferencePrefix + offerId;

    /// <summary>Base key for the first header, <c>:seq{N}</c> for every raise delta, where N is
    /// the count of existing headers — so a retried placement replays instead of double-holding.</summary>
    internal static string IdempotencyKeyFor(string offerId, int seq) =>
        seq <= 0 ? IdempotencyKeyPrefix + offerId : $"{IdempotencyKeyPrefix}{offerId}:seq{seq}";

    public async Task<HoldPlacement> PlaceOnSubmitAsync(
        Guid jeeberGuid, string jeeberId, string offerId, string requestId,
        decimal thisOfferCommission, CancellationToken ct)
    {
        if (thisOfferCommission <= 0m) return HoldPlacement.Ok();

        var headers = await TryReadHoldSetAsync(offerId, "place", ct);
        if (headers is null) return HoldPlacement.WalletUnavailable();

        // The sweeper backfills a SHORTFALL through this same path, so the record's expected
        // amount is what the set totals AFTER this leg, never just this leg.
        var heldTotal = headers.Where(h => h.IsPending).Sum(h => h.Amount);

        return await PlaceAsync(
            jeeberGuid, jeeberId, offerId, requestId, headers.Count,
            legAmount: thisOfferCommission, expectedTotal: heldTotal + thisOfferCommission,
            placedAt: _time.GetUtcNow(), operation: "place", ct);
    }

    public async Task<HoldPlacement> RaiseDeltaAsync(
        Guid jeeberGuid, string jeeberId, string offerId, string requestId,
        decimal newFeeCommissionTotal, CancellationToken ct)
    {
        var headers = await TryReadHoldSetAsync(offerId, "raise", ct);
        if (headers is null) return HoldPlacement.WalletUnavailable();

        var heldTotal = headers.Where(h => h.IsPending).Sum(h => h.Amount);
        var delta = newFeeCommissionTotal - heldTotal;
        if (delta <= 0m)
        {
            // DECISION Op 2: a lower fee places nothing and releases nothing — the surplus is
            // bounded by the prior raise and clears at the terminal release.
            _log.LogInformation(
                "hold.raise.noop offerId={OfferId} held={Held} required={Required}",
                offerId, heldTotal, newFeeCommissionTotal);
            return HoldPlacement.Ok();
        }

        var placedAt = (await TryGetIntentAsync(offerId, ct))?.PlacedAtUtc ?? _time.GetUtcNow();
        return await PlaceAsync(
            jeeberGuid, jeeberId, offerId, requestId, headers.Count,
            legAmount: delta, expectedTotal: newFeeCommissionTotal,
            placedAt: placedAt, operation: "raise", ct);
    }

    public async Task ReleaseForOfferAsync(string offerId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(offerId)) return;

        var externalReference = ExternalReferenceFor(offerId);

        IReadOnlyList<HoldHeader> headers;
        try
        {
            headers = await _wallet.ListByExternalReferenceAsync(externalReference, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the intent record: the sweeper retries what this pass could not read.
            _log.LogWarning(ex,
                "hold.release.set_read_failed offerId={OfferId} reason={Reason}; the intent record stands.",
                offerId, reason);
            return;
        }

        // Nothing held and nothing recorded: a release for an offer that never had a hold is a
        // no-op, so no tombstone is written for it.
        if (headers.Count == 0 && (await TryGetIntentAsync(offerId, ct)) is null) return;

        var aborted = 0;
        var failed = 0;
        foreach (var header in headers)
        {
            if (await SafeAbortAsync(header.TxId, offerId, reason, ct)) aborted++;
            else failed++;
        }

        if (failed > 0)
        {
            _log.LogWarning(
                "hold.release offerId={OfferId} reason={Reason} aborted={Aborted} failed={Failed}; "
                + "the intent record stands and the sweeper retries.",
                offerId, reason, aborted, failed);
            return;
        }

        _log.LogInformation(
            "hold.release offerId={OfferId} reason={Reason} aborted={Aborted} externalRef={ExternalRef}",
            offerId, reason, aborted, externalReference);

        await SafeCloseAsync(offerId, reason, ct);
    }

    public async Task ReleaseForRequestAsync(string requestId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;

        OfferReadResult<PendingOffer> read;
        try
        {
            read = await _offers.TryListForRequestAsync(requestId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "hold.release.enumeration_failed requestId={RequestId} reason={Reason}; the sweeper reconciles.",
                requestId, reason);
            return;
        }

        if (read.Degraded)
        {
            _log.LogWarning(
                "hold.release.enumeration_degraded requestId={RequestId} reason={Reason}; the sweeper reconciles.",
                requestId, reason);
            return;
        }

        await ReleaseEachAsync(read.Items, reason, live: true, ct);
    }

    public async Task ReleaseWithdrawnForJeeberAsync(string jeeberId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jeeberId)) return;

        OfferReadResult<PendingOffer> read;
        try
        {
            read = await _offers.TryListForJeeberAsync(jeeberId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "hold.release.enumeration_failed jeeberId={JeeberId} reason={Reason}; the sweeper reconciles.",
                jeeberId, reason);
            return;
        }

        if (read.Degraded)
        {
            _log.LogWarning(
                "hold.release.enumeration_degraded jeeberId={JeeberId} reason={Reason}; the sweeper reconciles.",
                jeeberId, reason);
            return;
        }

        // The status IS the confirmation: in production the bulk withdraw has no upstream route
        // (JEBV4-148), so releasing on the ATTEMPT would strip live offers of their collateral.
        await ReleaseEachAsync(read.Items, reason, live: false, ct);
    }

    public async Task AbortHoldSetForCaptureAsync(string offerId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(offerId)) return;

        var externalReference = ExternalReferenceFor(offerId);

        IReadOnlyList<HoldHeader> headers;
        try
        {
            headers = await _wallet.ListByExternalReferenceAsync(externalReference, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The accept saga is never unwound by a hold fault; the collector's own debit
            // reports the shortfall if the still-held funds refuse it.
            _log.LogError(ex,
                "hold.capture.set_read_failed offerId={OfferId}; the capture debit runs against a hold set "
                + "that could not be read.", offerId);
            return;
        }

        var failed = 0;
        foreach (var header in headers)
        {
            if (!await SafeAbortAsync(header.TxId, offerId, "capture", ct)) failed++;
        }

        if (failed > 0)
        {
            _log.LogError(
                "hold.capture.abort_failed offerId={OfferId} failed={Failed}; the held funds may still "
                + "block the capture debit.", offerId, failed);
            return;
        }

        _log.LogInformation(
            "hold.capture.aborted offerId={OfferId} headers={Headers} externalRef={ExternalRef}",
            offerId, headers.Count, externalReference);

        await SafeCloseAsync(offerId, "capture", ct);
    }

    /// <summary>Op 1/Op 2 core: intent record FIRST (invariant I2), then the pending leg, then
    /// the tx id back onto the record.</summary>
    private async Task<HoldPlacement> PlaceAsync(
        Guid jeeberGuid, string jeeberId, string offerId, string requestId, int seq,
        decimal legAmount, decimal expectedTotal, DateTimeOffset placedAt, string operation, CancellationToken ct)
    {
        var wallets = await ResolveWalletPairAsync(jeeberGuid, offerId, operation, ct);
        if (wallets is not { } pair) return HoldPlacement.WalletUnavailable();
        var (feeWallet, systemWallet) = pair;

        var intent = new HoldIntent(
            offerId, jeeberId, requestId, seq, expectedTotal, placedAt, null, HoldIntentState.Open);

        try
        {
            await _intents.WriteAsync(intent, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "hold.{Op}.intent_write_failed offerId={OfferId} jeeberId={JeeberId} amount={Amount}; "
                + "nothing was placed.", operation, offerId, jeeberId, legAmount);
            return HoldPlacement.Unresolvable();
        }

        var externalReference = ExternalReferenceFor(offerId);
        var idempotencyKey = IdempotencyKeyFor(offerId, seq);

        Guid txId;
        try
        {
            // Both legs are the configured fee currency (USD, invariant I4); isAdditionalFees is
            // false because this leg IS the fee, not a surcharge on top of one.
            txId = await _wallet.InitiateAsync(
                feeWallet, systemWallet, legAmount, HoldTag,
                notes: idempotencyKey, idempotencyKey, externalReference, isAdditionalFees: false, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsInsufficientBalance)
        {
            await MarkFailedAsync(intent, ct);
            _log.LogWarning(
                "hold.{Op}.insufficient offerId={OfferId} jeeberId={JeeberId} amount={Amount}; nothing held.",
                operation, offerId, jeeberId, legAmount);
            return HoldPlacement.InsufficientFunds();
        }
        catch (WalletCommissionDebitException ex)
        {
            await MarkFailedAsync(intent, ct);
            _log.LogError(ex,
                "hold.{Op}.initiate_failed offerId={OfferId} jeeberId={JeeberId} amount={Amount}; nothing held.",
                operation, offerId, jeeberId, legAmount);
            return HoldPlacement.WalletUnavailable();
        }

        try
        {
            await _intents.WriteAsync(intent with { TxId = txId }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The leg IS placed but its tx id is unrecorded: drop it now, and the intent record
            // written above keeps the sweeper able to find it if the abort also fails.
            await SafeAbortAsync(txId, offerId, "intent-write-failed", ct);
            _log.LogError(ex,
                "hold.{Op}.txid_write_failed offerId={OfferId} txId={TxId} amount={Amount}.",
                operation, offerId, txId, legAmount);
            return HoldPlacement.Unresolvable();
        }

        _log.LogInformation(
            "hold.{Op} offerId={OfferId} jeeberId={JeeberId} requestId={RequestId} seq={Seq} "
            + "amount={Amount} expectedTotal={ExpectedTotal} txId={TxId} externalRef={ExternalRef}",
            operation, offerId, jeeberId, requestId, seq, legAmount, expectedTotal, txId, externalReference);

        return HoldPlacement.Ok(txId);
    }

    /// <summary><paramref name="live"/> selects the side of the ledger this fan-out owns: live for a
    /// request that just died, terminal for offers a confirmed withdraw already retired.</summary>
    private async Task ReleaseEachAsync(
        IReadOnlyList<PendingOffer> offers, string reason, bool live, CancellationToken ct)
    {
        foreach (var offer in offers)
        {
            if (PendingOfferStatus.IsLive(offer.Status) != live) continue;
            await ReleaseForOfferAsync(offer.Id, reason, ct);
        }
    }

    public async Task RollbackLegAsync(string offerId, Guid txId, string reason, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(offerId) || txId == Guid.Empty) return;

        // A failed abort keeps the intent record open on purpose: that record is the only
        // thing that lets the sweeper find and drain this leg on a later pass.
        if (!await SafeAbortAsync(txId, offerId, reason, ct)) return;

        _log.LogInformation(
            "hold.rollback offerId={OfferId} txId={TxId} reason={Reason}", offerId, txId, reason);

        var headers = await TryReadHoldSetAsync(offerId, "rollback", ct);
        if (headers is not null && !headers.Any(h => h.IsPending))
        {
            await SafeCloseAsync(offerId, reason, ct);
        }
    }

    private async Task<(Guid Fee, Guid System)?> ResolveWalletPairAsync(
        Guid jeeberGuid, string offerId, string operation, CancellationToken ct)
    {
        try
        {
            var source = await _wallet.ResolveFeeWalletAsync(jeeberGuid, ct);
            if (source is not { } feeWallet)
            {
                _log.LogWarning(
                    "hold.{Op}.no_fee_wallet offerId={OfferId} holderId={HolderId}; no active non-COD "
                    + "wallet to hold against.", operation, offerId, jeeberGuid);
                return null;
            }

            var destination = await _wallet.ResolveSystemWalletAsync(ct);
            if (destination is not { } systemWallet)
            {
                _log.LogError(
                    "hold.{Op}.no_system_wallet offerId={OfferId}; the platform counterparty wallet is "
                    + "not provisioned.", operation, offerId);
                return null;
            }

            return (feeWallet, systemWallet);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "hold.{Op}.wallet_resolve_failed offerId={OfferId} holderId={HolderId}.",
                operation, offerId, jeeberGuid);
            return null;
        }
    }

    /// <summary>Null discriminates "the hold set could not be read" from an empty set.</summary>
    private async Task<IReadOnlyList<HoldHeader>?> TryReadHoldSetAsync(
        string offerId, string operation, CancellationToken ct)
    {
        try
        {
            return await _wallet.ListByExternalReferenceAsync(ExternalReferenceFor(offerId), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "hold.{Op}.set_read_failed offerId={OfferId}; the sequence number is unknown so nothing "
                + "is placed.", operation, offerId);
            return null;
        }
    }

    private async Task<HoldIntent?> TryGetIntentAsync(string offerId, CancellationToken ct)
    {
        try
        {
            return await _intents.GetAsync(offerId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "hold.intent.read_failed offerId={OfferId}.", offerId);
            return null;
        }
    }

    /// <summary>Best-effort: an unmarked record is collected by the sweeper's stale branch.</summary>
    private async Task MarkFailedAsync(HoldIntent intent, CancellationToken ct)
    {
        try
        {
            await _intents.WriteAsync(intent with { State = HoldIntentState.Failed }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "hold.intent.mark_failed_failed offerId={OfferId}.", intent.OfferId);
        }
    }

    private async Task<bool> SafeAbortAsync(Guid txId, string offerId, string reason, CancellationToken ct)
    {
        if (txId == Guid.Empty) return true;

        try
        {
            await _wallet.AbortAsync(txId, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Own event name: the frozen `hold.release` reason enum (CONTRACT §4) stays clean.
            _log.LogWarning(ex,
                "hold.abort_failed offerId={OfferId} txId={TxId} reason={Reason}.",
                offerId, txId, reason);
            return false;
        }
    }

    private async Task SafeCloseAsync(string offerId, string reason, CancellationToken ct)
    {
        try
        {
            await _intents.CloseAsync(offerId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "hold.release.close_failed offerId={OfferId} reason={Reason}; the funds ARE released and "
                + "the sweeper closes the record.", offerId, reason);
        }
    }

}
