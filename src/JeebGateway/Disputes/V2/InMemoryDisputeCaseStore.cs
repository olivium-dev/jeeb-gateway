using System.Collections.Concurrent;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// In-memory <see cref="IDisputeCaseStore"/>. Mirrors the shape that the
/// production Postgres implementation will own. All mutating operations
/// run under <c>_gate</c> so a racing escalate/resolve cannot observe a
/// torn read, and the lifecycle mutators (<see cref="ApplyReviewAsync"/> /
/// <see cref="ApplyCloseAsync"/>) re-validate the transition under that lock
/// (compare-and-set) so concurrent admin actions can never last-writer-wins.
///
/// TODO(ADR-0005, deferred): this is durable dispute-case domain state living
/// inside the gateway process. Per the owner standard "the gateway never stores
/// state" and all three PR reviews, the dispute lifecycle (open → under_review →
/// resolved_* → closed) belongs in the owning dispute/state microservice
/// (jeeb-state-service), with this in-memory store kept only as the
/// flag-off rollback fallback. The durable StateServiceDisputeCaseStore that
/// satisfies this same interface lands in the ADR-0005 state-service extraction
/// fold — it MUST move ListAllAsync / ApplyReviewAsync / ApplyCloseAsync in
/// lockstep with this class so the state-service path compiles and the
/// review/close semantics (ReviewedAt/ClosedAt columns) do not diverge.
/// Consequences of NOT extracting (tracked, not fixed here): the admin queue
/// and transition legality are per-process — lost on restart and inconsistent
/// across >1 gateway replica. FLAG: do not widen the gateway's stateful surface
/// further without the durable counterpart in the same change.
/// </summary>
public sealed class InMemoryDisputeCaseStore : IDisputeCaseStore
{
    private readonly ConcurrentDictionary<string, DisputeCase> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public Task<DisputeCase> AddAsync(DisputeCase @case, CancellationToken ct)
    {
        lock (_gate)
        {
            _byId[@case.Id] = @case;
            if (!string.IsNullOrEmpty(@case.IdempotencyKey))
            {
                _byIdempotencyKey[@case.IdempotencyKey] = @case.Id;
            }
        }
        return Task.FromResult(@case);
    }

    public Task<DisputeCase?> GetByIdAsync(string caseId, CancellationToken ct)
    {
        _byId.TryGetValue(caseId, out var c);
        return Task.FromResult(c);
    }

    public Task<DisputeCase?> GetByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct)
    {
        if (!_byIdempotencyKey.TryGetValue(idempotencyKey, out var id))
        {
            return Task.FromResult<DisputeCase?>(null);
        }
        _byId.TryGetValue(id, out var c);
        return Task.FromResult(c);
    }

    public Task<DisputeCase?> GetActiveForDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        var match = _byId.Values
            .Where(c => string.Equals(c.DeliveryId, deliveryId, StringComparison.Ordinal)
                        && !DisputeCaseState.IsResolved(c.State))
            .OrderByDescending(c => c.OpenedAt)
            .FirstOrDefault();
        return Task.FromResult(match);
    }

    public Task<IReadOnlyList<DisputeCase>> ListForUserAsync(string userId, CancellationToken ct)
    {
        var items = _byId.Values
            .Where(c => string.Equals(c.OpenedByUserId, userId, StringComparison.Ordinal)
                        || string.Equals(c.CounterpartyUserId, userId, StringComparison.Ordinal))
            .OrderByDescending(c => c.OpenedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<DisputeCase>>(items);
    }

    public Task<IReadOnlyList<DisputeCase>> ListAllAsync(string? state, CancellationToken ct)
    {
        var items = _byId.Values
            .Where(c => string.IsNullOrWhiteSpace(state)
                        || string.Equals(c.State, state, StringComparison.Ordinal))
            .OrderByDescending(c => c.OpenedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<DisputeCase>>(items);
    }

    public Task<DisputeMutationResult> ApplyReviewAsync(string caseId, string reviewedByAdminId, DateTimeOffset reviewedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(caseId, out var existing))
            {
                return Task.FromResult(DisputeMutationResult.NotFound());
            }

            // Compare-and-set: re-validate the open → under_review transition
            // against the CURRENT state under the lock, not a snapshot the
            // service read before acquiring it. Two concurrent reviews on the
            // same open case both pass the service-level pre-check; this guard
            // lets exactly one win and returns the loser an InvalidTransition
            // (mapped to 409) instead of silently re-stamping ReviewedAt/admin.
            if (!DisputeCaseState.CanTransition(existing.State, DisputeCaseState.UnderReview))
            {
                return Task.FromResult(DisputeMutationResult.InvalidTransition(existing));
            }

            existing.State = DisputeCaseState.UnderReview;
            existing.ReviewedByAdminId = reviewedByAdminId;
            existing.ReviewedAt = reviewedAt;
            return Task.FromResult(DisputeMutationResult.Updated(existing));
        }
    }

    public Task<DisputeMutationResult> ApplyCloseAsync(string caseId, DateTimeOffset closedAt, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(caseId, out var existing))
            {
                return Task.FromResult(DisputeMutationResult.NotFound());
            }

            // Compare-and-set: re-validate resolved_* → closed under the lock.
            // Prevents a double-close and a close racing a resolve from both
            // applying (last-writer-wins on ClosedAt). The loser gets a 409.
            if (!DisputeCaseState.CanTransition(existing.State, DisputeCaseState.Closed))
            {
                return Task.FromResult(DisputeMutationResult.InvalidTransition(existing));
            }

            existing.State = DisputeCaseState.Closed;
            existing.ClosedAt = closedAt;
            return Task.FromResult(DisputeMutationResult.Updated(existing));
        }
    }

    public Task<DisputeCase?> ApplyResolutionAsync(string caseId, DisputeCaseResolutionPatch patch, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(caseId, out var existing))
            {
                return Task.FromResult<DisputeCase?>(null);
            }

            existing.State = patch.State;
            existing.ResolvedAt = patch.ResolvedAt;
            existing.ResolverAdminId = patch.ResolverAdminId;
            existing.ResolutionNotes = patch.ResolutionNotes;
            existing.RefundUsd = patch.RefundUsd;
            existing.RefundLedgerEntryId = patch.RefundLedgerEntryId;
            existing.ResolveIdempotencyKey = patch.ResolveIdempotencyKey;
            return Task.FromResult<DisputeCase?>(existing);
        }
    }

    public Task<DisputeCase?> ReplaceEvidenceAsync(string caseId, DisputeEvidence evidence, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(caseId, out var existing))
            {
                return Task.FromResult<DisputeCase?>(null);
            }
            existing.Evidence = evidence;
            return Task.FromResult<DisputeCase?>(existing);
        }
    }
}
