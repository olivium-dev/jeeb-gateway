using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Admin;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;
using SwTransactionRequest = JeebGateway.service.ServiceWallet.TransactionRequest;
using SwTransactionDetailsRequest = JeebGateway.service.ServiceWallet.TransactionDetailsRequest;
using SwWallet = JeebGateway.service.ServiceWallet.Wallet;
using SwWalletHolder = JeebGateway.service.ServiceWallet.WalletHolder;
using Transaction = JeebGateway.service.ServiceWallet.Transaction;

namespace JeebGateway.Partner;

/// <summary>
/// Default <see cref="IPartnerWalletService"/> — orchestrates the reused wallet-service saga.
/// ADR-0001: stateless money math (amounts are the caller's, fees are wallet-service's). Money moves
/// only inside wallet-service, but the gateway now owns the idempotency dedup + immutable audit of
/// each move (<see cref="IPartnerWalletOperationStore"/>) so a retried confirm can never double-move
/// real money, and every admin cash-in is durably attributed.
/// </summary>
public sealed class PartnerWalletService : IPartnerWalletService
{
    private readonly ServiceWalletClient _wallet;
    private readonly IPartnerWalletOperationStore _ops;
    private readonly IAdminAuditLog _audit;
    private readonly PartnerWalletOptions _options;
    private readonly ILogger<PartnerWalletService> _log;
    private readonly IReadOnlyCollection<string> _jeeberHolderTypes;
    private readonly IReadOnlyCollection<string> _partnerHolderTypes;

    public PartnerWalletService(
        ServiceWalletClient wallet,
        IPartnerWalletOperationStore ops,
        IAdminAuditLog audit,
        IOptions<PartnerWalletOptions> options,
        ILogger<PartnerWalletService> log)
    {
        _wallet = wallet;
        _ops = ops;
        _audit = audit;
        _options = options.Value;
        _log = log;
        _jeeberHolderTypes = SplitTokens(_options.JeeberHolderTypes);
        _partnerHolderTypes = SplitTokens(_options.PartnerHolderTypes);
    }

    public async Task<PartnerWalletBalanceResponse> GetPartnerBalanceAsync(Guid partnerId, CancellationToken ct)
    {
        var holder = await _wallet.WalletsAsync(partnerId);
        var wallet = PickWallet(holder?.Wallets);
        return new PartnerWalletBalanceResponse
        {
            PartnerId = partnerId,
            PartnerName = holder?.WalletHolder?.HolderName,
            Balance = wallet?.Amount ?? 0d,
            CurrencyId = wallet?.CurrencyID ?? _options.CurrencyId,
            IsActive = holder?.WalletHolder?.IsActive ?? false,
        };
    }

    public async Task<PartnerJeeberTargetResponse> ResolveJeeberTargetAsync(Guid jeeberId, CancellationToken ct)
    {
        try
        {
            var holder = await _wallet.WalletsAsync(jeeberId);
            var wallet = PickWallet(holder?.Wallets);
            return new PartnerJeeberTargetResponse
            {
                JeeberId = jeeberId,
                HasWallet = wallet is not null,
                JeeberName = holder?.WalletHolder?.HolderName,
            };
        }
        catch (WalletApiException ex) when (ex.StatusCode == 404)
        {
            // No holder provisioned yet → a valid, negative answer, not an error.
            return new PartnerJeeberTargetResponse { JeeberId = jeeberId, HasWallet = false };
        }
    }

    public async Task<PartnerTopupPreviewResponse> PredictTopupAsync(
        Guid partnerId, Guid jeeberId, double amount, CancellationToken ct)
    {
        var (sourceWalletId, _) = await RequireWalletAsync(partnerId, "partner", _partnerHolderTypes, ct);
        var (destWalletId, _) = await RequireWalletAsync(jeeberId, "jeeber", _jeeberHolderTypes, ct);

        var request = BuildRequest(sourceWalletId, destWalletId, amount, _options.TopupTag,
            notes: $"predict:{partnerId}->{jeeberId}");

        var expected = await _wallet.PredictAsync(request);
        var fees = expected?.Fees ?? 0d;
        var gross = expected?.GrossAmount ?? amount;
        return new PartnerTopupPreviewResponse
        {
            JeeberId = jeeberId,
            GrossAmount = gross,
            Fees = fees,
            NetToJeeber = gross - fees,
            Summary = expected?.Summary,
        };
    }

    public async Task<PartnerWalletMoveResponse> ExecuteTopupAsync(
        Guid partnerId, Guid jeeberId, double amount, string idempotencyKey, string? note,
        CancellationToken ct)
    {
        // Resolve wallets (+ BOPLA target-type guard) BEFORE claiming so a transient read failure
        // never leaves a pending idempotency claim behind.
        var (sourceWalletId, _) = await RequireWalletAsync(partnerId, "partner", _partnerHolderTypes, ct);
        var (destWalletId, _) = await RequireWalletAsync(jeeberId, "jeeber", _jeeberHolderTypes, ct);

        var notes = $"idem:{idempotencyKey}" + (string.IsNullOrWhiteSpace(note) ? "" : $";note:{note}");
        var request = BuildRequest(sourceWalletId, destWalletId, amount, _options.TopupTag, notes);

        // Fees preview captured for the receipt (wallet-service authoritative; the gateway never
        // computes them). Best-effort — a Predict blip must not block the confirmed move.
        var fees = await SafePredictFeesAsync(request);

        var key = new PartnerOperationKey(PartnerOperationType.Topup, partnerId, idempotencyKey);
        var intent = new PartnerOperationIntent(partnerId, jeeberId, amount, note);

        return await MoveMoneyIdempotentAsync(
            key, intent, request,
            txId =>
            {
                _log.LogInformation(
                    "Partner top-up executed: partner={PartnerId} jeeber={JeeberId} amount={Amount} tx={TxId} idem={Idem}",
                    partnerId, jeeberId, amount, txId, idempotencyKey);
                return new PartnerWalletMoveResponse
                {
                    TransactionId = txId, Amount = amount, Fees = fees, Status = "executed",
                };
            },
            ct);
    }

    public async Task<PartnerWalletMoveResponse> CreditPartnerFromCashAsync(
        Guid partnerId, Guid operatorId, double amount, string idempotencyKey, string evidenceNote,
        CancellationToken ct)
    {
        var (destWalletId, _) = await RequireWalletAsync(partnerId, "partner", _partnerHolderTypes, ct);
        var systemWalletId = await RequireSystemWalletIdAsync(ct);

        var request = BuildRequest(systemWalletId, destWalletId, amount, _options.CreditTag,
            notes: $"cash-credit;operator:{operatorId};evidence:{evidenceNote}");

        var key = new PartnerOperationKey(PartnerOperationType.CashCredit, operatorId, idempotencyKey);
        var intent = new PartnerOperationIntent(partnerId, null, amount, evidenceNote);

        var result = await MoveMoneyIdempotentAsync(
            key, intent, request,
            txId => new PartnerWalletMoveResponse
            {
                TransactionId = txId, Amount = amount, Fees = 0d, Status = "executed",
            },
            ct);

        // Durable, immutable admin cash-in audit (reuses the append-only admin_actions store — money
        // creation may never live only in a mutable log line that a rotation loses). The idempotency
        // row is itself a durable record; this adds the admin-timeline view. A durable claim already
        // exists, so an audit-append blip must not un-move committed money — it is logged, not fatal.
        try
        {
            await _audit.AppendAsync(new AdminAuditAppend
            {
                AdminUserId = operatorId.ToString(),
                Action = "partner.wallet.cash-credit",
                EntityType = "partner_wallet",
                EntityId = partnerId.ToString(),
                AfterState = new Dictionary<string, object?>
                {
                    ["amount"] = amount,
                    ["evidenceNote"] = evidenceNote,
                    ["transactionId"] = result.TransactionId.ToString(),
                    ["idempotencyKey"] = idempotencyKey,
                },
                RequestId = idempotencyKey,
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex,
                "Partner cash credit AUDIT append failed for partner={PartnerId} tx={TxId} operator={OperatorId}; "
                + "the money move + durable idempotency row already committed — reconcile the admin timeline.",
                partnerId, result.TransactionId, operatorId);
        }

        _log.LogInformation(
            "Partner cash credit recorded: operator={OperatorId} partner={PartnerId} amount={Amount} tx={TxId} evidence={Evidence}",
            operatorId, partnerId, amount, result.TransactionId, evidenceNote);

        return result;
    }

    // ── internals ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The money-safe idempotent orchestration shared by both money paths: claim the key, then
    /// (only if we won) run the saga and Complete/Release/Uncertain the claim. A Replay returns the
    /// prior committed result (money moved once); an in-flight/uncertain claim throws
    /// <see cref="PartnerWalletInFlightException"/> (409) — never a second execute.
    /// </summary>
    private async Task<PartnerWalletMoveResponse> MoveMoneyIdempotentAsync(
        PartnerOperationKey key,
        PartnerOperationIntent intent,
        SwTransactionRequest request,
        Func<Guid, PartnerWalletMoveResponse> buildResult,
        CancellationToken ct)
    {
        var claim = await _ops.TryClaimAsync(key, intent, ct);
        switch (claim.Kind)
        {
            case PartnerClaimKind.Replay:
                _log.LogInformation(
                    "Partner wallet op {Type} actor={Actor} idem={Idem} REPLAY — returning prior result "
                    + "tx={TxId}; money moves zero more times.",
                    key.Type, key.ActorId, key.IdempotencyKey, claim.Result!.TransactionId);
                return claim.Result!;

            case PartnerClaimKind.InFlight:
                throw new PartnerWalletInFlightException(
                    "A matching operation is already in progress or awaiting reconciliation; it was not "
                    + "re-executed.");
        }

        // Won the claim — we are the sole executor.
        Guid headerId;
        try
        {
            headerId = await RunSagaAsync(request, ct);
        }
        catch (PartnerWalletSagaException sx) when (sx.Kind == SagaFailureKind.PreCommit)
        {
            // Nothing committed → free the key so a genuine retry can re-claim cleanly.
            await _ops.ReleaseAsync(key, ct);
            throw sx.InnerException!; // surface the original upstream/precondition error
        }
        catch (PartnerWalletSagaException sx) // Uncertain — the move MAY have committed.
        {
            // Lock the key: any retry now returns 409 (never a second execute). Do NOT abort.
            await _ops.MarkUncertainAsync(key, ct);
            throw new PartnerWalletUncertainException(
                "The wallet move was submitted but its outcome is unconfirmed; it was NOT retried or "
                + "reversed automatically to avoid a double move. Please reconcile before resubmitting.",
                sx.InnerException);
        }

        var result = buildResult(headerId);
        await _ops.CompleteAsync(key, headerId, result, ct);
        return result;
    }

    /// <summary>
    /// Initiate → Execute. Classifies failures so the caller can act money-safely:
    /// <list type="bullet">
    ///   <item><b>Pre-commit</b> (initiate failed; no header returned; or Execute deterministically
    ///   4xx-REJECTED) — nothing committed. The initiated header is aborted to release it, and the key
    ///   may be freed for retry.</item>
    ///   <item><b>Uncertain</b> (Execute failed 5xx / timeout / transport) — the move MAY have
    ///   committed, so the header is DELIBERATELY NOT aborted (aborting a possibly-committed tx is the
    ///   double-move bug) and the key is locked for reconciliation.</item>
    /// </list>
    /// </summary>
    private async Task<Guid> RunSagaAsync(SwTransactionRequest request, CancellationToken ct)
    {
        Transaction initiated;
        try
        {
            initiated = await _wallet.InitiateAsync(request);
        }
        catch (Exception ex)
        {
            // Initiate failed → nothing was committed. Pure pre-commit; nothing to abort.
            throw new PartnerWalletSagaException(SagaFailureKind.PreCommit, Guid.Empty, ex);
        }

        var headerId = ResolveHeaderId(initiated);
        if (headerId == Guid.Empty)
        {
            throw new PartnerWalletSagaException(
                SagaFailureKind.PreCommit, Guid.Empty,
                new PartnerWalletException("wallet-service did not return a transaction id to execute."));
        }

        try
        {
            await _wallet.ExecuteAsync(headerId);
            return headerId;
        }
        catch (WalletApiException ex) when (ex.StatusCode is >= 400 and < 500)
        {
            // Deterministic wallet-service rejection (e.g. insufficient funds / invalid state): the
            // money did NOT move on this execute. Safe to abort the initiated header and retry.
            _log.LogWarning(ex,
                "Partner wallet saga execute REJECTED ({Status}) for tx {TxId}; aborting the initiated header.",
                ex.StatusCode, headerId);
            await SafeAbortAsync(headerId, ct);
            throw new PartnerWalletSagaException(SagaFailureKind.PreCommit, headerId, ex);
        }
        catch (Exception ex)
        {
            // Ambiguous (5xx / timeout / transport): the execute MAY have committed. Do NOT abort a
            // possibly-committed transaction — that is the money-double-move bug. Leave it for
            // reconciliation and surface an uncertain outcome.
            _log.LogError(ex,
                "Partner wallet saga execute AMBIGUOUS for tx {TxId}; NOT aborting (may have committed).",
                headerId);
            throw new PartnerWalletSagaException(SagaFailureKind.Uncertain, headerId, ex);
        }
    }

    private async Task SafeAbortAsync(Guid headerId, CancellationToken ct)
    {
        try
        {
            await _wallet.AbortAsync(headerId);
        }
        catch (Exception abortEx)
        {
            _log.LogError(abortEx, "Partner wallet saga ABORT also failed for tx {TxId}.", headerId);
        }
    }

    private async Task<double> SafePredictFeesAsync(SwTransactionRequest request)
    {
        try
        {
            var expected = await _wallet.PredictAsync(request);
            return expected?.Fees ?? 0d;
        }
        catch (WalletApiException ex)
        {
            _log.LogDebug(ex, "Partner top-up fee preview failed; continuing with fees=0 on the receipt.");
            return 0d;
        }
    }

    private SwTransactionRequest BuildRequest(
        Guid sourceWalletId, Guid destWalletId, double amount, string tag, string notes)
        => new()
        {
            ServiceName = _options.ServiceName,
            Tag = tag,
            Notes = notes,
            Transactions = new List<SwTransactionDetailsRequest>
            {
                new()
                {
                    SourceWalletId = sourceWalletId,
                    DestinationWalletId = destWalletId,
                    Amount = amount,
                    IsAdditionalFees = false,
                },
            },
        };

    /// <summary>
    /// Resolve the holder's wallet id AND enforce the BOPLA target-type guard (OWASP API3): when
    /// <see cref="PartnerWalletOptions.EnforceHolderType"/> is on, reject a holder whose
    /// wallet-service <c>HolderType</c> is present and NOT in <paramref name="expectedTypes"/>, so a
    /// partner can't direct money into an arbitrary holder GUID (another partner/customer/admin) and
    /// the "jeeber"/"partner" route names reflect the enforced constraint. An empty/unknown HolderType
    /// degrades open (logged), pending owner confirmation of wallet-service's holder-type vocabulary.
    /// </summary>
    private async Task<(Guid WalletId, SwWalletHolder? Holder)> RequireWalletAsync(
        Guid holderId, string label, IReadOnlyCollection<string> expectedTypes, CancellationToken ct)
    {
        var holder = await _wallet.WalletsAsync(holderId);
        var wallet = PickWallet(holder?.Wallets);
        if (wallet is null || wallet.WalletId == Guid.Empty)
        {
            throw new PartnerWalletException(
                $"The {label} has no provisioned wallet for currency {_options.CurrencyId}.");
        }

        if (_options.EnforceHolderType && expectedTypes.Count > 0)
        {
            var actual = holder?.WalletHolder?.HolderType;
            if (string.IsNullOrWhiteSpace(actual))
            {
                _log.LogWarning(
                    "Partner wallet target-type guard: {Label} holder {HolderId} has no HolderType; "
                    + "degrading OPEN (enforcement pending owner Q5 vocabulary confirmation).",
                    label, holderId);
            }
            else if (!expectedTypes.Contains(actual, StringComparer.OrdinalIgnoreCase))
            {
                _log.LogWarning(
                    "Partner wallet target-type guard REJECT: {Label} holder {HolderId} HolderType='{Actual}' "
                    + "is not an eligible {Label} target (expected one of: {Expected}).",
                    label, holderId, actual, string.Join(",", expectedTypes));
                throw new PartnerWalletException(
                    $"The specified holder is not an eligible {label} target for this operation.");
            }
        }

        return (wallet.WalletId, holder?.WalletHolder);
    }

    private async Task<Guid> RequireSystemWalletIdAsync(CancellationToken ct)
    {
        var system = await _wallet.SystemWalletAsync();
        var wallet = PickWallet(system?.Wallets);
        if (wallet is null || wallet.WalletId == Guid.Empty)
        {
            throw new PartnerWalletException("The system wallet is not provisioned for the configured currency.");
        }
        return wallet.WalletId;
    }

    /// <summary>Pick the holder's wallet for the configured currency; fall back to the first wallet.</summary>
    private SwWallet? PickWallet(IEnumerable<SwWallet>? wallets)
    {
        if (wallets is null) return null;
        var list = wallets as IReadOnlyList<SwWallet> ?? wallets.ToList();
        return list.FirstOrDefault(w => w.CurrencyID == _options.CurrencyId) ?? list.FirstOrDefault();
    }

    private static IReadOnlyCollection<string> SplitTokens(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Array.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static Guid ResolveHeaderId(Transaction? txn)
        => txn?.TransactionHeader?.TxId ?? Guid.Empty;
}
