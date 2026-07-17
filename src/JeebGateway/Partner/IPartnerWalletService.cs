using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// Thin orchestration seam over the reused wallet-service transaction saga
/// (predict → initiate → execute/abort) for the Jeeb Partner Portal. It resolves holder wallets,
/// previews fees, and moves money — but the AMOUNTS are always the caller's and the FEES are always
/// wallet-service's (ADR-0001 thin BFF; the gateway invents no money math). Controllers stay
/// stateless and delegate here so the saga's initiate/execute/abort ordering lives in one place.
/// </summary>
public interface IPartnerWalletService
{
    /// <summary>The partner's own wallet balance/summary, projected for the portal.</summary>
    Task<PartnerWalletBalanceResponse> GetPartnerBalanceAsync(Guid partnerId, CancellationToken ct);

    /// <summary>Whether a jeeber is a valid top-up destination (has a provisioned wallet).</summary>
    Task<PartnerJeeberTargetResponse> ResolveJeeberTargetAsync(Guid jeeberId, CancellationToken ct);

    /// <summary>Preview wallet-service fees for a partner→jeeber top-up (no money moves).</summary>
    Task<PartnerTopupPreviewResponse> PredictTopupAsync(
        Guid partnerId, Guid jeeberId, double amount, CancellationToken ct);

    /// <summary>
    /// Execute a partner→jeeber top-up via the wallet-service saga (initiate → execute; abort on
    /// any post-initiate failure). Commission/fees are realized by wallet-service Fees config and
    /// flow to the system wallet — the gateway does not compute or route them.
    /// </summary>
    Task<PartnerWalletMoveResponse> ExecuteTopupAsync(
        Guid partnerId, Guid jeeberId, double amount, string idempotencyKey, string? note,
        CancellationToken ct);

    /// <summary>
    /// Credit a partner wallet with cash an admin recorded offline: a system-wallet → partner-wallet
    /// saga move. Idempotency-keyed (a double-submit never double-creates money), evidence note is
    /// carried onto the wallet-service transaction, and a durable, immutable admin cash-in audit row is
    /// written (operator, partner, amount, evidence, txId) — a money-creation event may never be
    /// un-attributed, so <paramref name="operatorId"/> is a hard precondition resolved by the caller.
    /// </summary>
    Task<PartnerWalletMoveResponse> CreditPartnerFromCashAsync(
        Guid partnerId, Guid operatorId, double amount, string idempotencyKey, string evidenceNote,
        CancellationToken ct);
}
