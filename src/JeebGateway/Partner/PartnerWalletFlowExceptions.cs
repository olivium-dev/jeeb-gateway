using System;

namespace JeebGateway.Partner;

/// <summary>
/// Raised when a money move is refused because a matching idempotency claim is already pending or in
/// the uncertain post-execute state (<see cref="IPartnerWalletOperationStore"/>). The caller must NOT
/// retry the saga — controllers map this to <b>409 Conflict</b>. Money never moves a second time.
/// </summary>
public sealed class PartnerWalletInFlightException : Exception
{
    public PartnerWalletInFlightException(string message) : base(message)
    {
    }
}

/// <summary>
/// Raised when a wallet-service <c>Execute</c> failed AMBIGUOUSLY (5xx / timeout / transport) — the
/// move MAY have committed, so the gateway deliberately did NOT abort or retry it (aborting a
/// possibly-committed transaction is the money-double-move bug this fixes). Controllers map this to
/// <b>502 Bad Gateway</b>; the idempotency key is locked so any client retry is refused (409), never
/// re-executed, until an operator reconciles.
/// </summary>
public sealed class PartnerWalletUncertainException : Exception
{
    public PartnerWalletUncertainException(string message, Exception? inner) : base(message, inner)
    {
    }
}

/// <summary>How a wallet-service saga failed — decides whether the idempotency key is freed or locked.</summary>
internal enum SagaFailureKind
{
    /// <summary>Initiate failed, or Execute was deterministically REJECTED (4xx) — nothing committed. Safe to retry.</summary>
    PreCommit = 0,

    /// <summary>Execute failed ambiguously (5xx / timeout) — the move may have committed. Never auto-retry.</summary>
    Uncertain = 1,
}

/// <summary>
/// Internal saga classification carried out of <c>RunSagaAsync</c> so the idempotent orchestrator can
/// decide, money-safely, whether to <see cref="IPartnerWalletOperationStore.ReleaseAsync"/> the key
/// (pre-commit) or <see cref="IPartnerWalletOperationStore.MarkUncertainAsync"/> it (uncertain). Never
/// leaves the service — it is unwrapped into the original error (pre-commit) or a
/// <see cref="PartnerWalletUncertainException"/> before returning to controllers.
/// </summary>
internal sealed class PartnerWalletSagaException : Exception
{
    public PartnerWalletSagaException(SagaFailureKind kind, Guid headerId, Exception inner)
        : base(inner.Message, inner)
    {
        Kind = kind;
        HeaderId = headerId;
    }

    public SagaFailureKind Kind { get; }

    public Guid HeaderId { get; }
}
