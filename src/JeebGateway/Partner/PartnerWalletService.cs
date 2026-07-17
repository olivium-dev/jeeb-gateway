using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ServiceWalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;
using SwTransactionRequest = JeebGateway.service.ServiceWallet.TransactionRequest;
using SwTransactionDetailsRequest = JeebGateway.service.ServiceWallet.TransactionDetailsRequest;
using SwWallet = JeebGateway.service.ServiceWallet.Wallet;

namespace JeebGateway.Partner;

/// <summary>
/// Default <see cref="IPartnerWalletService"/> — orchestrates the reused wallet-service saga.
/// ADR-0001: stateless, no persistence, money only ever moves inside wallet-service.
/// </summary>
public sealed class PartnerWalletService : IPartnerWalletService
{
    private readonly ServiceWalletClient _wallet;
    private readonly PartnerWalletOptions _options;
    private readonly ILogger<PartnerWalletService> _log;

    public PartnerWalletService(
        ServiceWalletClient wallet,
        IOptions<PartnerWalletOptions> options,
        ILogger<PartnerWalletService> log)
    {
        _wallet = wallet;
        _options = options.Value;
        _log = log;
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
        var sourceWalletId = await RequireWalletIdAsync(partnerId, "partner", ct);
        var destWalletId = await RequireWalletIdAsync(jeeberId, "jeeber", ct);

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
        var sourceWalletId = await RequireWalletIdAsync(partnerId, "partner", ct);
        var destWalletId = await RequireWalletIdAsync(jeeberId, "jeeber", ct);

        var notes = $"idem:{idempotencyKey}" + (string.IsNullOrWhiteSpace(note) ? "" : $";note:{note}");
        var request = BuildRequest(sourceWalletId, destWalletId, amount, _options.TopupTag, notes);

        // Fees preview captured for the receipt (wallet-service authoritative; the gateway never
        // computes them). Best-effort — a Predict blip must not block the confirmed move.
        var fees = await SafePredictFeesAsync(request);

        var result = await RunSagaAsync(request, ct);

        _log.LogInformation(
            "Partner top-up executed: partner={PartnerId} jeeber={JeeberId} amount={Amount} tx={TxId} idem={Idem}",
            partnerId, jeeberId, amount, result, idempotencyKey);

        return new PartnerWalletMoveResponse { TransactionId = result, Amount = amount, Fees = fees, Status = "executed" };
    }

    public async Task<PartnerWalletMoveResponse> CreditPartnerFromCashAsync(
        Guid partnerId, double amount, string evidenceNote, CancellationToken ct)
    {
        var destWalletId = await RequireWalletIdAsync(partnerId, "partner", ct);
        var systemWalletId = await RequireSystemWalletIdAsync(ct);

        var request = BuildRequest(systemWalletId, destWalletId, amount, _options.CreditTag,
            notes: $"cash-credit;evidence:{evidenceNote}");

        var result = await RunSagaAsync(request, ct);

        // Audit trail: an admin money-in event is always logged with the evidence note, the operator
        // is resolved by the controller from the bearer and included there.
        _log.LogInformation(
            "Partner cash credit recorded: partner={PartnerId} amount={Amount} tx={TxId} evidence={Evidence}",
            partnerId, amount, result, evidenceNote);

        return new PartnerWalletMoveResponse { TransactionId = result, Amount = amount, Fees = 0d, Status = "executed" };
    }

    // ── internals ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Initiate → Execute; Abort on any post-initiate failure so a half-open transaction is never
    /// left dangling. Returns the wallet-service transaction-header id.
    /// </summary>
    private async Task<Guid> RunSagaAsync(SwTransactionRequest request, CancellationToken ct)
    {
        var initiated = await _wallet.InitiateAsync(request);
        var headerId = ResolveHeaderId(initiated);
        if (headerId == Guid.Empty)
        {
            throw new PartnerWalletException("wallet-service did not return a transaction id to execute.");
        }

        try
        {
            await _wallet.ExecuteAsync(headerId);
            return headerId;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Partner wallet saga execute failed for tx {TxId}; aborting.", headerId);
            try
            {
                await _wallet.AbortAsync(headerId);
            }
            catch (Exception abortEx)
            {
                _log.LogError(abortEx, "Partner wallet saga ABORT also failed for tx {TxId}.", headerId);
            }
            throw;
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

    private async Task<Guid> RequireWalletIdAsync(Guid holderId, string label, CancellationToken ct)
    {
        var holder = await _wallet.WalletsAsync(holderId);
        var wallet = PickWallet(holder?.Wallets);
        if (wallet is null || wallet.WalletId == Guid.Empty)
        {
            throw new PartnerWalletException($"The {label} has no provisioned wallet for currency {_options.CurrencyId}.");
        }
        return wallet.WalletId;
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

    private static Guid ResolveHeaderId(service.ServiceWallet.Transaction? txn)
        => txn?.TransactionHeader?.TxId ?? Guid.Empty;
}
