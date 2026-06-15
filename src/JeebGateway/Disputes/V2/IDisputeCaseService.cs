namespace JeebGateway.Disputes.V2;

/// <summary>
/// T-BE-028 / JEB-64 dispute-case orchestration.
///
/// <list type="bullet">
///   <item><see cref="EscalateAsync"/> creates a case, synchronously
///     attaches chat transcript and GPS polyline as evidence (AC1) and
///     fan-outs notifications to both parties.</item>
///   <item><see cref="GetAsync"/> returns a single case for the caller
///     when they opened it (or for either party of the delivery, or
///     admin).</item>
///   <item><see cref="ListForUserAsync"/> returns every case the caller
///     either opened or is a counter-party on (AC4).</item>
///   <item><see cref="ResolveAsync"/> applies an admin's decision. When
///     <c>RefundUsd &gt; 0</c> the orchestrator hits
///     <c>unified_payment_gateway</c> through
///     <see cref="JeebGateway.Services.Clients.IPaymentRefundClient"/>
///     and rolls back state if the refund fails (AC2 + PO blocker #4).
///     A second resolve on a terminal case throws
///     <see cref="DisputeCaseConflictException"/> (AC3).</item>
/// </list>
/// </summary>
public interface IDisputeCaseService
{
    Task<EscalateResult> EscalateAsync(EscalateInput input, CancellationToken ct);

    Task<DisputeCase?> GetAsync(string caseId, CancellationToken ct);

    Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// JEB-64 admin queue (T-CMS-004): returns every case, optionally
    /// filtered to one <paramref name="state"/> (null = full queue).
    /// Admin-scoped at the controller (dispute.read.queue, AdminOnly).
    /// </summary>
    Task<IReadOnlyList<DisputeCase>> ListAllAsync(string? state, CancellationToken ct);

    /// <summary>
    /// JEB-64 state machine: an admin claims an <c>open</c> case for triage
    /// (<c>open → under_review</c>). NotFound when unknown; InvalidTransition
    /// when the case is not <c>open</c> (already under_review / resolved).
    /// </summary>
    Task<ReviewResult> ReviewAsync(ReviewCaseInput input, CancellationToken ct);

    /// <summary>
    /// JEB-64 terminal seal: an admin closes a resolved case
    /// (<c>resolved_* → closed</c>). NotFound when unknown; InvalidTransition
    /// when the case is not in a resolved-* state (e.g. closing an open case).
    /// </summary>
    Task<CloseResult> CloseAsync(CloseCaseInput input, CancellationToken ct);

    Task<ResolveResult> ResolveAsync(ResolveCaseInput input, CancellationToken ct);
}

public sealed class ReviewCaseInput
{
    public required string CaseId { get; init; }
    public required string AdminUserId { get; init; }
}

public enum ReviewOutcome
{
    Reviewed,
    NotFound,
    InvalidTransition
}

public sealed record ReviewResult(ReviewOutcome Outcome, DisputeCase? Case);

public sealed class CloseCaseInput
{
    public required string CaseId { get; init; }
    public required string AdminUserId { get; init; }
}

public enum CloseOutcome
{
    Closed,
    NotFound,
    InvalidTransition
}

public sealed record CloseResult(CloseOutcome Outcome, DisputeCase? Case);

public sealed class EscalateInput
{
    public required string DeliveryId { get; init; }
    public required string OpenedByUserId { get; init; }
    public required string Reason { get; init; }
    public string? Comment { get; init; }
    public IReadOnlyList<string> PhotoUrls { get; init; } = Array.Empty<string>();
    public string? IdempotencyKey { get; init; }
}

public enum EscalateOutcome
{
    Created,
    Replayed,
    DeliveryNotFound,
    AlreadyEscalated,

    /// <summary>
    /// JEB-64 / S14 N2: the opener is neither the client nor the assigned
    /// jeeber of the delivery. Only a party to a delivery may escalate it
    /// → 403 not-a-party. (Admin-on-behalf escalation is a separate, not
    /// yet built, surface.)
    /// </summary>
    NotAParty
}

public sealed record EscalateResult(EscalateOutcome Outcome, DisputeCase? Case);

public enum ResolveDecision
{
    Refund,
    NoAction
}

public sealed class ResolveCaseInput
{
    public required string CaseId { get; init; }
    public required string AdminUserId { get; init; }
    public required ResolveDecision Decision { get; init; }
    public decimal? RefundUsd { get; init; }
    public string? Notes { get; init; }
    public string? IdempotencyKey { get; init; }
}

public enum ResolveOutcome
{
    Resolved,
    Replayed,
    NotFound,
    AlreadyResolved,
    RefundFailed
}

public sealed record ResolveResult(ResolveOutcome Outcome, DisputeCase? Case, string? FailureReason);

public sealed class DisputeCaseValidationException : Exception
{
    public DisputeCaseValidationException(string message) : base(message) { }
}

public sealed class DisputeCaseConflictException : Exception
{
    public DisputeCaseConflictException(string message) : base(message) { }
}
