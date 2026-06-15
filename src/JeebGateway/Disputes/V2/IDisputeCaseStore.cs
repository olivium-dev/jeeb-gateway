namespace JeebGateway.Disputes.V2;

/// <summary>
/// Persistence seam for v2 dispute cases. MVP backs this with an
/// in-memory ConcurrentDictionary; production swaps in a Postgres
/// implementation colocated with the admin moderation tables.
/// </summary>
public interface IDisputeCaseStore
{
    Task<DisputeCase> AddAsync(DisputeCase @case, CancellationToken ct);

    Task<DisputeCase?> GetByIdAsync(string caseId, CancellationToken ct);

    /// <summary>
    /// Returns the case associated with <paramref name="idempotencyKey"/>
    /// at escalate time, or null when the key has not been seen before.
    /// Used by <see cref="IDisputeCaseService.EscalateAsync"/> to make
    /// /escalate replay-safe (PO review blocker #6).
    /// </summary>
    Task<DisputeCase?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Returns the currently-open (non-resolved) case for a delivery, or
    /// null. Enforces "one active case per delivery" at the service layer.
    /// </summary>
    Task<DisputeCase?> GetActiveForDeliveryAsync(string deliveryId, CancellationToken ct);

    Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// JEB-64 admin queue (T-CMS-004): returns every case, optionally
    /// filtered to a single <paramref name="state"/>. Admin-scoped — the
    /// caller-side capability gate (dispute.read.queue, AdminOnly) ensures
    /// only admins reach this. <paramref name="state"/> null = full queue.
    /// </summary>
    Task<IReadOnlyList<DisputeCase>> ListAllAsync(string? state, CancellationToken ct);

    /// <summary>
    /// JEB-64 state machine: claims an <c>open</c> case for triage
    /// (<c>open → under_review</c>). The <c>open → under_review</c> legality
    /// check is performed as a compare-and-set INSIDE the store's lock, so a
    /// racing review/close/resolve on the same case cannot both pass an
    /// out-of-lock pre-check and then both apply (last-writer-wins). Returns
    /// <see cref="DisputeMutationOutcome.Updated"/> with the new row,
    /// <see cref="DisputeMutationOutcome.NotFound"/> when the id is unknown,
    /// or <see cref="DisputeMutationOutcome.InvalidTransition"/> when the case
    /// is no longer <c>open</c> at the moment the lock is taken.
    /// </summary>
    Task<DisputeMutationResult> ApplyReviewAsync(string caseId, string reviewedByAdminId, DateTimeOffset reviewedAt, CancellationToken ct);

    /// <summary>
    /// JEB-64 terminal seal: closes a resolved case
    /// (<c>resolved_* → closed</c>). The <c>resolved_* → closed</c> legality
    /// check is a compare-and-set INSIDE the store's lock (same atomicity
    /// guarantee as <see cref="ApplyReviewAsync"/>). Returns
    /// <see cref="DisputeMutationOutcome.Updated"/> /
    /// <see cref="DisputeMutationOutcome.NotFound"/> /
    /// <see cref="DisputeMutationOutcome.InvalidTransition"/>.
    /// </summary>
    Task<DisputeMutationResult> ApplyCloseAsync(string caseId, DateTimeOffset closedAt, CancellationToken ct);

    /// <summary>
    /// Persists a resolution. Returns the updated row, or null when
    /// the id is unknown. The update runs under the store's lock so a
    /// concurrent second-resolver cannot observe a stale state.
    /// </summary>
    Task<DisputeCase?> ApplyResolutionAsync(string caseId, DisputeCaseResolutionPatch patch, CancellationToken ct);

    /// <summary>
    /// Atomically replaces the row's evidence bundle. Used by the
    /// orchestrator when a degraded escalate is later back-filled by
    /// the admin queue refresh hook.
    /// </summary>
    Task<DisputeCase?> ReplaceEvidenceAsync(string caseId, DisputeEvidence evidence, CancellationToken ct);
}

/// <summary>
/// Outcome of an atomic dispute-case lifecycle mutation
/// (<see cref="IDisputeCaseStore.ApplyReviewAsync"/> /
/// <see cref="IDisputeCaseStore.ApplyCloseAsync"/>). Distinguishes the three
/// terminal states of a compare-and-set under the store lock so the service
/// can map them to the correct HTTP status without ever conflating
/// "unknown id" (404) with "illegal concurrent move" (409).
/// </summary>
public enum DisputeMutationOutcome
{
    /// <summary>The transition was legal and applied; <see cref="DisputeMutationResult.Case"/> is the updated row.</summary>
    Updated,

    /// <summary>No case exists for the supplied id.</summary>
    NotFound,

    /// <summary>
    /// The case exists but the transition was not legal from its CURRENT state
    /// (re-validated under the lock). This is the compare-and-set "lost the
    /// race" signal — the service surfaces it as 409 invalid-transition.
    /// <see cref="DisputeMutationResult.Case"/> carries the current row so the
    /// caller can report the actual state it found.
    /// </summary>
    InvalidTransition
}

/// <summary>
/// Result of an atomic lifecycle mutation. <see cref="Case"/> is non-null for
/// <see cref="DisputeMutationOutcome.Updated"/> (the new row) and
/// <see cref="DisputeMutationOutcome.InvalidTransition"/> (the conflicting
/// current row); null for <see cref="DisputeMutationOutcome.NotFound"/>.
/// </summary>
public readonly record struct DisputeMutationResult(DisputeMutationOutcome Outcome, DisputeCase? Case)
{
    public static DisputeMutationResult Updated(DisputeCase @case) => new(DisputeMutationOutcome.Updated, @case);
    public static DisputeMutationResult NotFound() => new(DisputeMutationOutcome.NotFound, null);
    public static DisputeMutationResult InvalidTransition(DisputeCase current) => new(DisputeMutationOutcome.InvalidTransition, current);
}

public sealed class DisputeCaseResolutionPatch
{
    public required string State { get; init; }
    public required DateTimeOffset ResolvedAt { get; init; }
    public required string ResolverAdminId { get; init; }
    public string? ResolutionNotes { get; init; }
    public decimal? RefundUsd { get; init; }
    public string? RefundLedgerEntryId { get; init; }
    public string? ResolveIdempotencyKey { get; init; }
}
