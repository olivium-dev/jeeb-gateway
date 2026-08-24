using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// O1 (owner ruling 2026-08-16, amended same day) — the platform-fee collection gate.
///
/// <para><b>OFF by default, on purpose.</b> Merging this must not start moving money. The flag has an
/// explicit value in <c>appsettings.json</c> (not merely a missing key, which is how
/// <c>CodSettlementMode</c> stayed inert and unnoticed) and every disabled pass increments
/// <c>settlement.commission.skipped</c>, so "silently never ran" is observable rather than invisible.</para>
/// </summary>
public sealed class CommissionCollectionOptions
{
    public const string SectionName = "CommissionCollection";

    /// <summary>Master switch. <c>false</c> = compute and observe, never debit. Owner-gated.</summary>
    public bool Enabled { get; set; }

    /// <summary>Opaque wallet-service <c>Tag</c>; surfaces as the ledger entry type. Wallet never
    /// branches on it, so it stays generic rather than product vocabulary.</summary>
    public string Tag { get; set; } = "platform-fee";

    /// <summary>Wallet-service currency id the fee is debited in. Must match <c>PartnerWallet:CurrencyId</c>
    /// — <c>BffStartupValidator</c> fails startup on a mismatch in Production.</summary>
    public int CurrencyId { get; set; } = 2;

    /// <summary>Display code paired with <see cref="CurrencyId"/> (2 = USD); the wallet guard
    /// returns it as the fee currency on every balance answer.</summary>
    public string CurrencyCode { get; set; } = "USD";
}

/// <summary>Everything the accept transition already knows. No settlement row exists yet.</summary>
public sealed record CommissionCollectionCommand(
    string RequestId,
    string JeeberId,
    decimal AcceptedFee);

public enum CommissionCollectionOutcome
{
    /// <summary>The owner gate is off. Nothing was read, nothing was moved.</summary>
    Disabled,

    /// <summary>No fee to take (non-positive accepted price, or an unusable holder id).</summary>
    NotCollectable,

    /// <summary>The fee moved from the jeeber's fee wallet to the platform wallet.</summary>
    Collected,

    /// <summary>wallet-service refused: the jeeber cannot cover the fee.</summary>
    InsufficientFunds,

    /// <summary>The jeeber has no active, non-COD wallet in the configured currency.</summary>
    NoFeeWallet,

    /// <summary>The platform counterparty wallet is not provisioned.</summary>
    NoSystemWallet,

    /// <summary>Same accept key, different money. Refused rather than charged a second time.</summary>
    IdempotencyConflict,

    /// <summary>The execute may or may not have committed. Deliberately NOT aborted.</summary>
    Uncertain,

    /// <summary>A deterministic upstream fault before any money could move.</summary>
    Failed,
}

public sealed record CommissionCollectionResult(
    CommissionCollectionOutcome Outcome,
    decimal Amount,
    Guid TransactionId,
    string? Detail = null);

public interface ICommissionCollector
{
    /// <summary>Never throws. An accept whose fee cannot be taken still ACCEPTS — the auction has
    /// already committed a winner and there is nothing left to abort.</summary>
    Task<CommissionCollectionResult> CollectOnAcceptAsync(
        CommissionCollectionCommand command, CancellationToken ct);

    /// <summary>Pure read plus a first-stamp-wins stamp. Moves no money; links a settlement row to
    /// the accept-time debit so the books join up. Never throws.</summary>
    Task LinkSettlementAsync(Settlement settlement, CancellationToken ct);
}

/// <summary>
/// Debits the platform commission from the jeeber's fee wallet into the platform (<c>__SYSTEM__</c>)
/// wallet at the moment an offer is ACCEPTED, then links the later settlement row to that debit.
///
/// <para><b>Exactly-once with zero gateway state.</b> The idempotency key is derived from the request
/// id, so a replayed accept sends the identical key and wallet-service's unique index returns the
/// original transaction instead of creating a second one. The gateway persists nothing.</para>
///
/// <para><b>Amount.</b> <see cref="WalletGuardContract.RequiredCommission"/> — literally the same
/// expression the offer-time and accept-time sufficiency guards check against, so what is checked and
/// what is charged cannot drift, and both match settlement-service's later booking (Q-001, flat 10%).</para>
/// </summary>
public sealed class WalletCommissionCollector : ICommissionCollector
{
    /// <summary>Prefix of the value stamped into the settlement's external ref.</summary>
    public const string ExternalRefPrefix = "wallet-tx:";

    private readonly IWalletCommissionDebitClient _wallet;
    private readonly ISettlementServiceClient _settlements;
    private readonly CommissionCollectionOptions _options;
    private readonly ILogger<WalletCommissionCollector> _log;

    public WalletCommissionCollector(
        IWalletCommissionDebitClient wallet,
        ISettlementServiceClient settlements,
        IOptions<CommissionCollectionOptions> options,
        ILogger<WalletCommissionCollector> log)
    {
        _wallet = wallet;
        _settlements = settlements;
        _options = options.Value;
        _log = log;
    }

    /// <summary>Stable and unique for the accept EVENT: one request has exactly one winner, so a
    /// replay reuses the key and a second, different accept is refused rather than charged.</summary>
    public static string IdempotencyKeyFor(string requestId) => $"accept:{requestId}";

    /// <summary>Derivable from any durable row that knows the delivery id — no gateway state needed
    /// to find the debit again later (the settlement link and any future reconciler both use it).</summary>
    public static string ExternalReferenceFor(string requestId) => $"delivery:{requestId}";

    public async Task<CommissionCollectionResult> CollectOnAcceptAsync(
        CommissionCollectionCommand command, CancellationToken ct)
    {
        var amount = WalletGuardContract.RequiredCommission(command.AcceptedFee);

        if (!_options.Enabled)
        {
            BusinessOutcomeTelemetry.CommissionCollectionSkipped.Add(1);
            _log.LogInformation(
                "commission.accept.skipped requestId={RequestId} jeeberId={JeeberId} acceptedFee={Fee} "
                + "amount={Amount} reason=disabled — CommissionCollection:Enabled is false, so the fee "
                + "is OWED and NOT COLLECTED (O1, owner-gated).",
                command.RequestId, command.JeeberId, command.AcceptedFee, amount);
            return new CommissionCollectionResult(CommissionCollectionOutcome.Disabled, amount, Guid.Empty);
        }

        if (amount <= 0m)
        {
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NotCollectable, amount, Guid.Empty, "no positive accepted fee");
        }

        if (!Guid.TryParse(command.JeeberId, out var holderId) || holderId == Guid.Empty)
        {
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NotCollectable, amount, Guid.Empty, "holder id is not a wallet holder");
        }

        try
        {
            return await RunAsync(command, holderId, amount, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The accept saga already committed a winner; a collection fault must never unwind it.
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "commission.accept.failed requestId={RequestId} amount={Amount}; the accept stands and "
                + "the fee was NOT collected.", command.RequestId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Failed, amount, Guid.Empty, ex.Message);
        }
    }

    private async Task<CommissionCollectionResult> RunAsync(
        CommissionCollectionCommand command, Guid holderId, decimal amount, CancellationToken ct)
    {
        // Rung 3 of the owner ruling: the fee wallet is a fee account. A COD leg can never be the
        // source, so cash-on-delivery float cannot pay the platform fee.
        var source = await _wallet.ResolveFeeWalletAsync(holderId, ct);
        if (source is not { } sourceWalletId)
        {
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogWarning(
                "commission.accept.no_fee_wallet requestId={RequestId} holderId={HolderId} "
                + "currencyId={CurrencyId}; no active non-COD wallet to debit.",
                command.RequestId, holderId, _options.CurrencyId);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NoFeeWallet, amount, Guid.Empty);
        }

        var destination = await _wallet.ResolveSystemWalletAsync(ct);
        if (destination is not { } systemWalletId)
        {
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(
                "commission.accept.no_system_wallet requestId={RequestId} currencyId={CurrencyId}; "
                + "the platform counterparty wallet is not provisioned.",
                command.RequestId, _options.CurrencyId);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NoSystemWallet, amount, Guid.Empty);
        }

        var idempotencyKey = IdempotencyKeyFor(command.RequestId);
        var externalReference = ExternalReferenceFor(command.RequestId);

        Guid transactionId;
        try
        {
            transactionId = await _wallet.InitiateAsync(
                sourceWalletId, systemWalletId, amount, _options.Tag,
                notes: idempotencyKey, idempotencyKey, externalReference, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsInsufficientBalance)
        {
            // wallet-service refuses an unaffordable debit at INITIATE, not only at execute.
            return Insufficient(command, holderId, amount, Guid.Empty, ex);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsIdempotencyConflict)
        {
            // Same accept key, different money: an accounting divergence, never a retry. Refuse.
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "commission.accept.idempotency_conflict requestId={RequestId} amount={Amount}; this "
                + "accept key already carries a DIFFERENT amount. Nothing charged — reconcile by hand.",
                command.RequestId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.IdempotencyConflict, amount, Guid.Empty, ex.Message);
        }
        catch (WalletCommissionDebitException ex)
        {
            // Initiate failed => nothing committed. Nothing to abort, nothing to reverse.
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "commission.accept.initiate_failed requestId={RequestId} amount={Amount}; no money moved.",
                command.RequestId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Failed, amount, Guid.Empty, ex.Message);
        }

        try
        {
            await _wallet.ExecuteAsync(transactionId, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsDeterministicRejection)
        {
            // Deterministic refusal: the money did NOT move, so releasing the pending header is safe
            // — and required, because wallet-service never expires one.
            await SafeAbortAsync(transactionId, command.RequestId, ct);
            if (ex.IsInsufficientBalance) return Insufficient(command, holderId, amount, transactionId, ex);

            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "commission.accept.execute_rejected requestId={RequestId} txId={TxId} amount={Amount}.",
                command.RequestId, transactionId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Failed, amount, transactionId, ex.Message);
        }
        catch (WalletCommissionDebitException ex)
        {
            // Ambiguous: the execute MAY have committed, so it is deliberately NOT aborted —
            // aborting a possibly-committed move is the double-move bug (ADR-0011).
            BusinessOutcomeTelemetry.CommissionCollectionUncertain.Add(1);
            _log.LogError(ex,
                "commission.accept.uncertain requestId={RequestId} txId={TxId} amount={Amount}; NOT "
                + "aborted — a re-drive replays the same idempotency key safely.",
                command.RequestId, transactionId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Uncertain, amount, transactionId, ex.Message);
        }

        BusinessOutcomeTelemetry.CommissionCollected.Add(1);
        _log.LogInformation(
            "commission.accept.collected requestId={RequestId} holderId={HolderId} acceptedFee={Fee} "
            + "amount={Amount} txId={TxId} externalRef={ExternalRef}",
            command.RequestId, holderId, command.AcceptedFee, amount, transactionId, externalReference);

        return new CommissionCollectionResult(
            CommissionCollectionOutcome.Collected, amount, transactionId);
    }

    /// <summary>
    /// Joins the settlement row to the accept-time debit by wallet-service's opaque external
    /// reference — a READ plus a first-stamp-wins stamp, never a money move. A row that ends up
    /// unstamped is precisely a delivery that settled without its fee ever being collected.
    /// </summary>
    public async Task LinkSettlementAsync(Settlement settlement, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(settlement.WalletTxId)) return;

        try
        {
            var txId = await _wallet.FindByExternalReferenceAsync(
                ExternalReferenceFor(settlement.DeliveryId), ct);
            if (txId is null)
            {
                BusinessOutcomeTelemetry.CommissionUnlinkedSettlements.Add(1);
                _log.LogWarning(
                    "commission.settle.unlinked settlementId={SettlementId} deliveryId={DeliveryId} "
                    + "commission={Commission}; no accept-time debit carries this delivery's reference, "
                    + "so this delivery settled with its fee UNCOLLECTED.",
                    settlement.Id, settlement.DeliveryId, settlement.Total);
                return;
            }

            var stamped = await _settlements.StampExternalRefAsync(
                settlement.Id, ExternalRefPrefix + txId.Value.ToString("D"), ct);
            settlement.WalletTxId = stamped?.WalletTxId;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            BusinessOutcomeTelemetry.CommissionStampFailures.Add(1);
            _log.LogError(ex,
                "commission.settle.link_failed settlementId={SettlementId} deliveryId={DeliveryId}; the "
                + "settle stands and no money was touched — the audit link can be rebuilt from the ledger.",
                settlement.Id, settlement.DeliveryId);
        }
    }

    private CommissionCollectionResult Insufficient(
        CommissionCollectionCommand command, Guid holderId, decimal amount, Guid transactionId,
        WalletCommissionDebitException ex)
    {
        BusinessOutcomeTelemetry.CommissionCollectionInsufficient.Add(1);
        _log.LogWarning(ex,
            "commission.accept.insufficient requestId={RequestId} holderId={HolderId} amount={Amount}; "
            + "the accept STANDS and the platform fee is UNCOLLECTED.",
            command.RequestId, holderId, amount);
        return new CommissionCollectionResult(
            CommissionCollectionOutcome.InsufficientFunds, amount, transactionId, ex.Message);
    }

    private async Task SafeAbortAsync(Guid transactionId, string requestId, CancellationToken ct)
    {
        try
        {
            await _wallet.AbortAsync(transactionId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "commission.accept.abort_failed requestId={RequestId} txId={TxId}; the pending hold was "
                + "not released and wallet-service never expires one.", requestId, transactionId);
        }
    }
}
