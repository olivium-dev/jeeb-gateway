using System;
using System.Threading;
using System.Threading.Tasks;

namespace JeebGateway.Partner;

/// <summary>
/// Money-safety idempotency / dedup store for the Partner Portal's two money-moving BFF paths
/// (partner→jeeber top-up and admin cash-credit). The wallet-service <c>TransactionRequest</c> has
/// NO idempotency field (see ServiceWalletClient — <c>ServiceName</c>/<c>Tag</c>/<c>Notes</c> only),
/// so a retried confirm (client timeout after the first commit) would otherwise re-run the
/// initiate→execute saga and DOUBLE-MOVE real money. This store makes the client-supplied
/// idempotency key a REAL dedup key enforced BEFORE the saga, and also serves as the durable,
/// immutable record of every money-in/move event (OWASP API6:2023 sensitive-flow replay guard;
/// JEBV4 partner-wallet-bff blocker set).
///
/// <para><b>Money-safety contract.</b> A claim is scoped to
/// <c>(operationType, actorId, idempotencyKey)</c> — the actor is the caller partner (top-up) or the
/// acting admin (cash-credit), so one actor's key can never collide with another's:
/// <list type="bullet">
///   <item><see cref="PartnerClaimKind.Won"/> — the caller is the FIRST to claim this key; it alone
///   runs the saga, then calls <see cref="CompleteAsync"/> (success), <see cref="ReleaseAsync"/>
///   (confirmed pre-commit failure — key freed so a genuine retry may proceed), or
///   <see cref="MarkUncertainAsync"/> (ambiguous post-execute failure — key LOCKED so no retry ever
///   re-executes a possibly-committed move).</item>
///   <item><see cref="PartnerClaimKind.Replay"/> — a prior claim already COMPLETED; the stored result
///   is returned verbatim and the money moves exactly once.</item>
///   <item><see cref="PartnerClaimKind.InFlight"/> — a prior claim is pending or in the uncertain
///   post-execute state; the caller must NOT run the saga (maps to 409). Never a second execute.</item>
/// </list></para>
///
/// <para>Mirrors the money-adjacent <see cref="JeebGateway.Financials.ISettlementEnqueueStore"/>
/// idempotency store: a durable Postgres impl (DB-level <c>UNIQUE + ON CONFLICT DO NOTHING</c>) in
/// prod, an in-memory impl for dev/CI/test, and a fail-closed
/// <see cref="JeebGateway.Infrastructure.StoreDurabilityGuard"/> registration so a mis-provisioned
/// prod gateway refuses to boot rather than silently double-spend from process memory.</para>
/// </summary>
public interface IPartnerWalletOperationStore
{
    /// <summary>
    /// Claim <paramref name="key"/> for a money move. On <see cref="PartnerClaimKind.Won"/> the
    /// caller owns execution; on <see cref="PartnerClaimKind.Replay"/> the returned
    /// <see cref="PartnerOperationClaim.Result"/> is the prior committed result; on
    /// <see cref="PartnerClaimKind.InFlight"/> the caller must not execute.
    /// </summary>
    Task<PartnerOperationClaim> TryClaimAsync(
        PartnerOperationKey key, PartnerOperationIntent intent, CancellationToken ct);

    /// <summary>Mark a Won claim COMPLETED with the wallet-service transaction id + result (replayable).</summary>
    Task CompleteAsync(
        PartnerOperationKey key, Guid transactionId, PartnerWalletMoveResponse result, CancellationToken ct);

    /// <summary>
    /// Release a still-pending Won claim after a CONFIRMED pre-commit failure (nothing moved) so a
    /// genuine retry with the same key can re-claim cleanly. Never deletes a completed row.
    /// </summary>
    Task ReleaseAsync(PartnerOperationKey key, CancellationToken ct);

    /// <summary>
    /// Lock a Won claim in the UNCERTAIN state after an ambiguous post-execute failure (the move MAY
    /// have committed). Future claims for the same key return <see cref="PartnerClaimKind.InFlight"/>
    /// (409) — never a second execute — until an operator reconciles.
    /// </summary>
    Task MarkUncertainAsync(PartnerOperationKey key, CancellationToken ct);
}

/// <summary>Which money-moving path a claim belongs to (namespaces the idempotency key).</summary>
public enum PartnerOperationType
{
    /// <summary>partner→jeeber top-up (POST v1/partner/wallet/transfers).</summary>
    Topup = 0,

    /// <summary>admin system→partner cash-credit (POST v1/admin/partners/{id}/wallet/credits).</summary>
    CashCredit = 1,
}

/// <summary>Kind of claim outcome — drives the caller's next move (execute / replay / 409).</summary>
public enum PartnerClaimKind
{
    /// <summary>First claim — the caller alone runs the saga.</summary>
    Won = 0,

    /// <summary>A prior claim already committed — return its stored result; move money zero more times.</summary>
    Replay = 1,

    /// <summary>A prior claim is pending / uncertain — do not execute (409).</summary>
    InFlight = 2,
}

/// <summary>The dedup key: an operation kind + the acting principal + the client idempotency key.</summary>
public sealed record PartnerOperationKey(PartnerOperationType Type, Guid ActorId, string IdempotencyKey);

/// <summary>
/// The durable, immutable facts recorded for a money-in/move event (satisfies the "immutable cash-in
/// audit record" requirement: operator/actor, partner, amount, evidence, txId, timestamps).
/// </summary>
public sealed record PartnerOperationIntent(
    Guid PartnerId, Guid? CounterpartyId, double Amount, string? EvidenceNote);

/// <summary>The result of a claim attempt.</summary>
public sealed record PartnerOperationClaim(PartnerClaimKind Kind, PartnerWalletMoveResponse? Result);
