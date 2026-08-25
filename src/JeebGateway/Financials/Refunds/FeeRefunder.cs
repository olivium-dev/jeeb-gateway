using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials.Refunds;

/// <summary>OD-P1 (supersedes ADR-0011's no-refund stance) — the compensating credit that gives a
/// captured platform fee back when the delivery it was taken for is cancelled.</summary>
public interface IFeeRefunder
{
    /// <summary>Never throws. The cancellation outcome the user sees is computed BEFORE and
    /// independently of this call; a refund fault must never fail a cancel.</summary>
    Task RefundOnCancelAsync(string requestId, string? jeeberId, string cancelledBy, CancellationToken ct);

    /// <summary>The sweeper's re-drive of a recorded intent. True once the refund is SETTLED
    /// (credited, proven already credited, provably never captured, or parked as a conflict).</summary>
    Task<bool> TryRetryAsync(RefundIntent intent, CancellationToken ct);
}

/// <summary>Credits the captured platform fee back on a post-accept cancel. Keyed on the LEDGER and
/// never the flag; amount and legs are the capture's, swapped (rationale: ADR-0012 Decision 2).</summary>
public sealed class FeeRefunder : IFeeRefunder
{
    /// <summary>Frozen wallet-service naming (CONTRACT §4) — deliberately distinct from the capture
    /// tag <c>platform-fee</c> and the hold tag <c>hold</c>.</summary>
    public const string RefundTag = "platform-fee-refund";

    internal const string IdempotencyKeyPrefix = "refund:";

    private readonly IWalletCommissionDebitClient _wallet;
    private readonly IRefundIntentStore _intents;
    private readonly CommissionCollectionOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<FeeRefunder> _log;

    public FeeRefunder(
        IWalletCommissionDebitClient wallet,
        IRefundIntentStore intents,
        IOptions<CommissionCollectionOptions> options,
        TimeProvider time,
        ILogger<FeeRefunder> log)
    {
        _wallet = wallet;
        _intents = intents;
        _options = options.Value;
        _time = time;
        _log = log;
    }

    /// <summary>Pairs with the capture key <c>accept:{requestId}</c>: one delivery, one refund, and a
    /// replay returns wallet-service's original transaction instead of a second credit.</summary>
    public static string IdempotencyKeyFor(string requestId) => IdempotencyKeyPrefix + requestId;

    public async Task RefundOnCancelAsync(
        string requestId, string? jeeberId, string cancelledBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;

        try
        {
            await RunAsync(requestId, jeeberId, cancelledBy, fromIntent: false, ct);
        }
        // No OCE re-throw: an abort must land in the counted path, never vanish untraced (W5-F1).
        catch (Exception ex)
        {
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.failed requestId={RequestId} cancelledBy={CancelledBy}; the cancellation "
                + "STANDS and any captured fee is UNREFUNDED.", requestId, cancelledBy);
        }
    }

    public async Task<bool> TryRetryAsync(RefundIntent intent, CancellationToken ct)
    {
        if (intent is null || string.IsNullOrWhiteSpace(intent.RequestId)) return true;

        try
        {
            return await RunAsync(
                intent.RequestId, intent.JeeberId, intent.CancelledBy, fromIntent: true, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.retry_failed requestId={RequestId}; the intent stands and the next sweep "
                + "retries it.", intent.RequestId);
            return false;
        }
    }

    /// <summary>DESIGN §2b steps 1-6. Returns true when the refund is settled and the intent needs
    /// no further sweep; false leaves the record open for the next pass.</summary>
    private async Task<bool> RunAsync(
        string requestId, string? jeeberId, string cancelledBy, bool fromIntent, CancellationToken ct)
    {
        var externalReference = WalletCommissionCollector.ExternalReferenceFor(requestId);
        var holder = jeeberId ?? string.Empty;

        IReadOnlyList<FeeLedgerEntry> ledger;
        try
        {
            ledger = await _wallet.ListFeeLedgerByExternalReferenceAsync(externalReference, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The ledger is the whole decision, so an unread ledger decides nothing: record the
            // owed refund and let the sweeper re-read it rather than guessing either way.
            await DeferAsync(requestId, holder, cancelledBy, ex, ct);
            return false;
        }

        var capture = FindExecuted(ledger, _options.Tag);
        if (capture is not { } captured)
        {
            // The flag-off money-neutrality surface: nothing was captured, so nothing is credited
            // and not one wallet mutation is issued.
            BusinessOutcomeTelemetry.FeeRefundSkipped.Add(1);
            _log.LogInformation(
                "fee.refund.skipped requestId={RequestId} jeeberId={JeeberId} cancelledBy={CancelledBy} "
                + "reason=no-capture externalRef={ExternalRef}",
                requestId, holder, cancelledBy, externalReference);

            if (fromIntent) await SafeCloseAsync(requestId, ct);
            return true;
        }

        if (FindExecuted(ledger, RefundTag) is { } existing)
        {
            _log.LogInformation(
                "fee.refund.replay requestId={RequestId} jeeberId={JeeberId} amount={Amount} txId={TxId} "
                + "cancelledBy={CancelledBy}; already credited, no second credit.",
                requestId, holder, existing.Amount, existing.TxId, cancelledBy);

            await SafeCloseAsync(requestId, ct);
            return true;
        }

        return await CreditAsync(requestId, holder, cancelledBy, captured, externalReference, ct);
    }

    /// <summary>Step 4-6: durable record FIRST (invariant I2), then the single-leg credit.</summary>
    private async Task<bool> CreditAsync(
        string requestId, string jeeberId, string cancelledBy, FeeLedgerEntry capture,
        string externalReference, CancellationToken ct)
    {
        var intent = new RefundIntent(
            requestId, jeeberId, capture.Amount, cancelledBy, _time.GetUtcNow(), null, RefundIntentState.Open);
        var recorded = await TryWriteIntentAsync(intent, "open", ct);

        // Legs are the capture's, swapped: __SYSTEM__ back to the jeeber's fee wallet. Never
        // re-resolved, so a changed wallet set cannot misroute the credit.
        var source = capture.DestinationWalletId;
        var destination = capture.SourceWalletId;
        if (source == Guid.Empty || destination == Guid.Empty)
        {
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(
                "fee.refund.unroutable requestId={RequestId} txId={TxId} amount={Amount} recorded={Recorded}; "
                + "the capture carries no usable wallet pair, so nothing was credited.",
                requestId, capture.TxId, capture.Amount, recorded);
            return false;
        }

        var idempotencyKey = IdempotencyKeyFor(requestId);

        Guid txId;
        try
        {
            txId = await _wallet.InitiateAsync(
                source, destination, capture.Amount, RefundTag,
                notes: idempotencyKey, idempotencyKey, externalReference, isAdditionalFees: false, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsIdempotencyConflict)
        {
            // Same refund key, different money: an accounting divergence, never a retry.
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.idempotency_conflict requestId={RequestId} amount={Amount}; this refund key "
                + "already carries a DIFFERENT amount. Nothing credited — reconcile by hand.",
                requestId, capture.Amount);

            await TryWriteIntentAsync(intent with { State = RefundIntentState.Conflict }, "conflict", ct);
            return true;
        }
        catch (WalletCommissionDebitException ex)
        {
            // Initiate failed => nothing committed. Nothing to abort; the record stays open.
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.initiate_failed requestId={RequestId} amount={Amount} recorded={Recorded}; "
                + "no money moved and the sweeper retries.", requestId, capture.Amount, recorded);
            return false;
        }

        await TryWriteIntentAsync(intent with { TxId = txId }, "txid", ct);

        try
        {
            await _wallet.ExecuteAsync(txId, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsDeterministicRejection)
        {
            // Deterministic refusal: the money did NOT move, so releasing the pending header is
            // safe — and required, because wallet-service never expires one.
            await SafeAbortAsync(txId, requestId, ct);
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.execute_rejected requestId={RequestId} txId={TxId} amount={Amount}; nothing "
                + "credited and the sweeper retries.", requestId, txId, capture.Amount);
            return false;
        }
        catch (WalletCommissionDebitException ex)
        {
            // Ambiguous: the execute MAY have committed, so it is deliberately NOT aborted —
            // aborting a possibly-committed move is the double-move bug (ADR-0011).
            _log.LogError(ex,
                "fee.refund.uncertain requestId={RequestId} txId={TxId} amount={Amount}; NOT aborted — "
                + "the retry replays the same idempotency key safely.", requestId, txId, capture.Amount);
            return false;
        }

        BusinessOutcomeTelemetry.FeeRefundCredited.Add(1);
        _log.LogInformation(
            "fee.refund requestId={RequestId} jeeberId={JeeberId} amount={Amount} txId={TxId} "
            + "cancelledBy={CancelledBy} externalRef={ExternalRef}",
            requestId, jeeberId, capture.Amount, txId, cancelledBy, externalReference);

        await SafeCloseAsync(requestId, ct);
        return true;
    }

    /// <summary>First EXECUTED header carrying this tag and a positive amount; a zero-amount or
    /// unclassifiable header is never treated as a capture.</summary>
    private static FeeLedgerEntry? FindExecuted(IReadOnlyList<FeeLedgerEntry> ledger, string tag)
    {
        foreach (var entry in ledger)
        {
            if (!entry.IsExecuted || entry.Amount <= 0m) continue;
            if (string.Equals(entry.Tag?.Trim(), tag, StringComparison.OrdinalIgnoreCase)) return entry;
        }

        return null;
    }

    private async Task DeferAsync(
        string requestId, string jeeberId, string cancelledBy, Exception cause, CancellationToken ct)
    {
        var intent = new RefundIntent(
            requestId, jeeberId, 0m, cancelledBy, _time.GetUtcNow(), null, RefundIntentState.Open);

        if (await TryWriteIntentAsync(intent, "deferred", ct))
        {
            _log.LogWarning(cause,
                "fee.refund.deferred requestId={RequestId} jeeberId={JeeberId} cancelledBy={CancelledBy}; "
                + "the fee ledger could not be read, so the sweeper decides on a later pass.",
                requestId, jeeberId, cancelledBy);
            return;
        }

        // Neither the ledger nor the record: this refund would be invisible, so it is counted.
        BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
        _log.LogError(cause,
            "fee.refund.deferred_unrecorded requestId={RequestId} jeeberId={JeeberId}; the ledger read "
            + "failed AND the intent could not be written — reconcile from the wallet ledger.",
            requestId, jeeberId);
    }

    private async Task<bool> TryWriteIntentAsync(RefundIntent intent, string stage, CancellationToken ct)
    {
        try
        {
            await _intents.WriteAsync(intent, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The credit still goes ahead: the idempotency key makes a re-drive safe, and a
            // silently-skipped refund is worse than an unrecorded one that is counted.
            BusinessOutcomeTelemetry.FeeRefundFailures.Add(1);
            _log.LogError(ex,
                "fee.refund.intent_write_failed requestId={RequestId} stage={Stage} state={State}.",
                intent.RequestId, stage, intent.State);
            return false;
        }
    }

    private async Task SafeAbortAsync(Guid txId, string requestId, CancellationToken ct)
    {
        if (txId == Guid.Empty) return;

        try
        {
            await _wallet.AbortAsync(txId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "fee.refund.abort_failed requestId={RequestId} txId={TxId}; the pending header was not "
                + "released and wallet-service never expires one.", requestId, txId);
        }
    }

    private async Task SafeCloseAsync(string requestId, CancellationToken ct)
    {
        try
        {
            await _intents.CloseAsync(requestId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The money IS back; only the record is stale, and the next sweep converges on the
            // ledger pre-check (fee.refund.replay) and closes it.
            _log.LogWarning(ex, "fee.refund.close_failed requestId={RequestId}.", requestId);
        }
    }
}
