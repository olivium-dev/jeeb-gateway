using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Financials;

/// <summary>
/// O1 (owner ruling 2026-08-16) — the platform-fee collection gate.
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

    /// <summary>Wallet-service currency id the fee is debited in. Matches <c>PartnerWallet:CurrencyId</c>.</summary>
    public int CurrencyId { get; set; } = 1;
}

public enum CommissionCollectionOutcome
{
    /// <summary>The owner gate is off. Nothing was read, nothing was moved.</summary>
    Disabled,

    /// <summary>No fee to take (pending intent, zero commission, or an unusable holder id).</summary>
    NotCollectable,

    /// <summary>The settlement already carries a wallet transaction reference.</summary>
    AlreadyCollected,

    /// <summary>The fee moved from the jeeber's fee wallet to the platform wallet.</summary>
    Collected,

    /// <summary>Wallet-service refused the execute: the jeeber cannot cover the fee.</summary>
    InsufficientFunds,

    /// <summary>The jeeber has no active, non-COD wallet in the configured currency.</summary>
    NoFeeWallet,

    /// <summary>The platform counterparty wallet is not provisioned.</summary>
    NoSystemWallet,

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
    /// <summary>Never throws. A settled delivery whose fee cannot be taken STAYS settled — the
    /// customer already paid cash and the handover already happened.</summary>
    Task<CommissionCollectionResult> CollectAsync(Settlement settlement, CancellationToken ct);
}

/// <summary>
/// Debits the platform commission from the jeeber's fee wallet into the platform (<c>__SYSTEM__</c>)
/// wallet, then stamps the wallet transaction id back onto the settlement row.
///
/// <para><b>Exactly-once with zero gateway state.</b> The idempotency key is derived from the
/// settlement id and sent to wallet-service, whose unique index on it is the durable dedupe. The
/// settlement's <c>external_ref</c> (first-stamp-wins) is the durable "already collected" marker.
/// The gateway stores nothing, which is the standing no-state-on-the-gateway rule.</para>
///
/// <para><b>Amount.</b> Taken verbatim from the settlement row, never recomputed. settlement-service
/// owns the arithmetic (owner ruling Q-001, flat 10%), so the booked fee and the collected fee
/// cannot drift.</para>
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

    /// <summary>Stable across re-drives, unique per settlement — wallet-service's durable dedupe key.</summary>
    public static string IdempotencyKeyFor(string settlementId) => $"settlement:{settlementId}";

    public async Task<CommissionCollectionResult> CollectAsync(Settlement settlement, CancellationToken ct)
    {
        var amount = settlement.Total;

        if (!_options.Enabled)
        {
            BusinessOutcomeTelemetry.CommissionCollectionSkipped.Add(1);
            _log.LogInformation(
                "settlement.commission.skipped settlementId={SettlementId} deliveryId={DeliveryId} "
                + "amount={Amount} reason=disabled — CommissionCollection:Enabled is false, so the fee "
                + "is BOOKED and NOT COLLECTED (O1, owner-gated).",
                settlement.Id, settlement.DeliveryId, amount);
            return new CommissionCollectionResult(CommissionCollectionOutcome.Disabled, amount, Guid.Empty);
        }

        if (amount <= 0m)
        {
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NotCollectable, amount, Guid.Empty, "no commission booked");
        }

        if (!string.IsNullOrWhiteSpace(settlement.WalletTxId))
        {
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.AlreadyCollected, amount, Guid.Empty, settlement.WalletTxId);
        }

        if (!Guid.TryParse(settlement.JeeberId, out var holderId) || holderId == Guid.Empty)
        {
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NotCollectable, amount, Guid.Empty, "holder id is not a wallet holder");
        }

        try
        {
            return await RunAsync(settlement, holderId, amount, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The settle already committed upstream; a collection fault must never unwind it.
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "settlement.commission.failed settlementId={SettlementId} deliveryId={DeliveryId} "
                + "amount={Amount}; the delivery stays settled and the fee was NOT collected.",
                settlement.Id, settlement.DeliveryId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Failed, amount, Guid.Empty, ex.Message);
        }
    }

    private async Task<CommissionCollectionResult> RunAsync(
        Settlement settlement, Guid holderId, decimal amount, CancellationToken ct)
    {
        // Rung 3 of the owner ruling: the fee wallet is a fee account. A COD leg can never be the
        // source, so cash-on-delivery float cannot pay the platform fee.
        var source = await _wallet.ResolveFeeWalletAsync(holderId, ct);
        if (source is not { } sourceWalletId)
        {
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogWarning(
                "settlement.commission.no_fee_wallet settlementId={SettlementId} holderId={HolderId} "
                + "currencyId={CurrencyId}; no active non-COD wallet to debit.",
                settlement.Id, holderId, _options.CurrencyId);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NoFeeWallet, amount, Guid.Empty);
        }

        var destination = await _wallet.ResolveSystemWalletAsync(ct);
        if (destination is not { } systemWalletId)
        {
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(
                "settlement.commission.no_system_wallet settlementId={SettlementId} currencyId={CurrencyId}; "
                + "the platform counterparty wallet is not provisioned.",
                settlement.Id, _options.CurrencyId);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.NoSystemWallet, amount, Guid.Empty);
        }

        var idempotencyKey = IdempotencyKeyFor(settlement.Id);
        var notes = $"{idempotencyKey};delivery:{settlement.DeliveryId}";

        Guid transactionId;
        try
        {
            transactionId = await _wallet.InitiateAsync(
                sourceWalletId, systemWalletId, amount, _options.Tag, notes, idempotencyKey, ct);
        }
        catch (WalletCommissionDebitException ex)
        {
            // Initiate failed => nothing committed. Nothing to abort, nothing to reverse.
            BusinessOutcomeTelemetry.CommissionCollectionFailures.Add(1);
            _log.LogError(ex,
                "settlement.commission.initiate_failed settlementId={SettlementId} amount={Amount}; "
                + "no money moved.", settlement.Id, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Failed, amount, Guid.Empty, ex.Message);
        }

        try
        {
            await _wallet.ExecuteAsync(transactionId, ct);
        }
        catch (WalletCommissionDebitException ex) when (ex.IsDeterministicRejection)
        {
            // Deterministic refusal (insufficient balance is the expected one): the money did NOT
            // move, so releasing the pending header is safe — and required, or the hold never expires.
            await SafeAbortAsync(transactionId, settlement.Id, ct);
            BusinessOutcomeTelemetry.CommissionCollectionInsufficient.Add(1);
            _log.LogWarning(ex,
                "settlement.commission.insufficient settlementId={SettlementId} holderId={HolderId} "
                + "amount={Amount}; the delivery stays settled and the platform fee is UNCOLLECTED.",
                settlement.Id, holderId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.InsufficientFunds, amount, transactionId, ex.Message);
        }
        catch (WalletCommissionDebitException ex)
        {
            // Ambiguous: the execute MAY have committed, so it is deliberately NOT aborted and
            // NOT stamped — aborting a possibly-committed move is the double-move bug (ADR-0011).
            BusinessOutcomeTelemetry.CommissionCollectionUncertain.Add(1);
            _log.LogError(ex,
                "settlement.commission.uncertain settlementId={SettlementId} txId={TxId} amount={Amount}; "
                + "NOT aborted and NOT stamped — reconcile before re-driving.",
                settlement.Id, transactionId, amount);
            return new CommissionCollectionResult(
                CommissionCollectionOutcome.Uncertain, amount, transactionId, ex.Message);
        }

        await StampAsync(settlement, transactionId, ct);

        BusinessOutcomeTelemetry.CommissionCollected.Add(1);
        _log.LogInformation(
            "settlement.commission.collected settlementId={SettlementId} deliveryId={DeliveryId} "
            + "holderId={HolderId} amount={Amount} txId={TxId}",
            settlement.Id, settlement.DeliveryId, holderId, amount, transactionId);

        return new CommissionCollectionResult(
            CommissionCollectionOutcome.Collected, amount, transactionId);
    }

    /// <summary>The durable "already collected" marker. Money has already moved, so a stamp fault is
    /// logged and counted, never rethrown — it degrades to a reconcilable row, not a lost debit.</summary>
    private async Task StampAsync(Settlement settlement, Guid transactionId, CancellationToken ct)
    {
        try
        {
            var stamped = await _settlements.StampExternalRefAsync(
                settlement.Id, ExternalRefPrefix + transactionId.ToString("D"), ct);
            settlement.WalletTxId = stamped?.WalletTxId;
        }
        catch (Exception ex)
        {
            BusinessOutcomeTelemetry.CommissionStampFailures.Add(1);
            _log.LogError(ex,
                "settlement.commission.stamp_failed settlementId={SettlementId} txId={TxId}; the fee WAS "
                + "collected but the settlement row does not record it — reconcile from the wallet ledger.",
                settlement.Id, transactionId);
        }
    }

    private async Task SafeAbortAsync(Guid transactionId, string settlementId, CancellationToken ct)
    {
        try
        {
            await _wallet.AbortAsync(transactionId, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "settlement.commission.abort_failed settlementId={SettlementId} txId={TxId}; the pending "
                + "hold was not released and wallet-service never expires one.",
                settlementId, transactionId);
        }
    }
}
