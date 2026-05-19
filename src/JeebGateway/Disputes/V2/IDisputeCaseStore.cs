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
