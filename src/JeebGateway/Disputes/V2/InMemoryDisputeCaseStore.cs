using System.Collections.Concurrent;

namespace JeebGateway.Disputes.V2;

/// <summary>
/// In-memory <see cref="IDisputeCaseStore"/>. Mirrors the shape that the
/// production Postgres implementation will own. All mutating operations
/// run under <c>_gate</c> so a racing escalate/resolve cannot observe a
/// torn read.
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
