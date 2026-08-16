using System.Text.Json;
using JeebGateway.StateService.Work;

namespace JeebGateway.UnitTests;

/// <summary>
/// In-process stand-in for state-service's work_items table. It deliberately survives the
/// disposal of a "gateway instance" so a restart can be simulated: the gateway is rebuilt,
/// this store is not. Semantics mirror JeebStateService.Persistence.OwnershipStore:
/// claim takes due_at &lt;= now with attempts &lt; max_attempts and charges one attempt;
/// defer refunds it (GREATEST(attempts - 1, 0)); every mutation is a version CAS.
/// </summary>
public sealed class FakeDurableWorkStore : IStateWorkItemClient
{
    private readonly Dictionary<Guid, Row> _rows = new();
    private readonly Dictionary<string, Guid> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _clock;

    public FakeDurableWorkStore(TimeProvider clock) => _clock = clock;

    /// <summary>Rows currently held, for assertions about what survived.</summary>
    public int Count { get { lock (_gate) { return _rows.Count; } } }

    public Task<StateWorkItem> CreateAsync(
        string idempotencyKey, StateWorkItemCreate request, CancellationToken ct)
    {
        lock (_gate)
        {
            var scoped = request.Application + "\u0000" + idempotencyKey;
            if (_byIdempotencyKey.TryGetValue(scoped, out var existing))
                return Task.FromResult(Snapshot(_rows[existing]));

            var now = _clock.GetUtcNow();
            var row = new Row
            {
                WorkItemId = Guid.NewGuid(),
                Application = request.Application,
                Kind = request.Kind,
                SubjectRef = request.SubjectRef,
                Status = "queued",
                Payload = request.Payload ?? default,
                DueAt = request.DueAt ?? now,
                MaxAttempts = request.MaxAttempts ?? 10,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _rows[row.WorkItemId] = row;
            _byIdempotencyKey[scoped] = row.WorkItemId;
            return Task.FromResult(Snapshot(row));
        }
    }

    public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_rows.TryGetValue(workItemId, out var row) ? Snapshot(row) : null);
        }
    }

    public Task<StateWorkItem?> GetLatestAsync(
        string application, string kind, string subjectRef, CancellationToken ct)
    {
        lock (_gate)
        {
            var latest = _rows.Values
                .Where(r => r.Application == application && r.Kind == kind && r.SubjectRef == subjectRef)
                .OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.WorkItemId)
                .FirstOrDefault();
            return Task.FromResult(latest is null ? null : Snapshot(latest));
        }
    }

    public Task<IReadOnlyList<StateWorkItem>> ClaimAsync(StateWorkClaim request, CancellationToken ct)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            var kinds = request.Kinds ?? Array.Empty<string>();
            var claimed = _rows.Values
                .Where(r => r.Application == request.Application)
                .Where(r => kinds.Count == 0 || kinds.Contains(r.Kind))
                .Where(r => r.DueAt <= now && r.Attempts < r.MaxAttempts)
                .Where(r => r.Status == "queued"
                            || (r.Status == "leased" && r.LeasedUntil <= now))
                .OrderBy(r => r.DueAt).ThenBy(r => r.CreatedAt)
                .Take(Math.Clamp(request.Limit ?? 10, 1, 100))
                .ToList();

            var result = new List<StateWorkItem>(claimed.Count);
            foreach (var row in claimed)
            {
                row.Status = "leased";
                row.LeaseToken = Guid.NewGuid();
                row.LeasedBy = request.WorkerId;
                row.LeasedUntil = now.AddSeconds(request.LeaseSeconds ?? 60);
                row.Attempts++;
                row.Version++;
                row.UpdatedAt = now;
                result.Add(Snapshot(row));
            }
            return Task.FromResult<IReadOnlyList<StateWorkItem>>(result);
        }
    }

    public Task<StateWorkItem> RenewLeaseAsync(
        Guid workItemId, StateWorkLeaseRenew request, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = Leased(workItemId, request.LeaseToken, request.ExpectedVersion, "lease_renew");
            row.LeasedUntil = _clock.GetUtcNow().AddSeconds(request.LeaseSeconds ?? 60);
            row.Version++;
            return Task.FromResult(Snapshot(row));
        }
    }

    public Task<StateWorkItem> CompleteAsync(
        Guid workItemId, StateWorkComplete request, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = Leased(workItemId, request.LeaseToken, request.ExpectedVersion, "complete");
            row.Status = "completed";
            row.Result = request.Result ?? default;
            row.ArtifactRef = request.ArtifactRef;
            row.ArtifactExpiresAt = request.ArtifactExpiresAt;
            row.DownloadTokenHash = request.DownloadTokenHash;
            row.CompletedAt = _clock.GetUtcNow();
            ClearLease(row);
            return Task.FromResult(Snapshot(row));
        }
    }

    public Task<StateWorkItem> DeferAsync(Guid workItemId, StateWorkDefer request, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = Leased(workItemId, request.LeaseToken, request.ExpectedVersion, "defer");
            row.Status = "queued";
            row.DueAt = request.DueAt;
            // Mirrors GREATEST(attempts - 1, 0): a deferral is attempt-neutral.
            row.Attempts = Math.Max(row.Attempts - 1, 0);
            row.LastError = request.Reason;
            row.CompletedAt = null;
            ClearLease(row);
            return Task.FromResult(Snapshot(row));
        }
    }

    public Task<StateWorkItem> FailAsync(Guid workItemId, StateWorkFail request, CancellationToken ct)
    {
        lock (_gate)
        {
            var row = Leased(workItemId, request.LeaseToken, request.ExpectedVersion, "fail");
            row.LastError = request.Error;
            if (request.RetryAt is { } retryAt)
            {
                row.Status = "queued";
                row.DueAt = retryAt;
            }
            else
            {
                row.Status = "failed";
                row.CompletedAt = _clock.GetUtcNow();
            }
            ClearLease(row);
            return Task.FromResult(Snapshot(row));
        }
    }

    public Task<StateWorkItem> ConsumeAsync(Guid workItemId, StateWorkConsume request, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_rows.TryGetValue(workItemId, out var row)
                || row.Status != "completed"
                || row.Version != request.ExpectedVersion
                || !string.Equals(row.DownloadTokenHash, request.DownloadTokenHash, StringComparison.Ordinal))
                throw new StateWorkConflictException("consume");

            row.Status = "consumed";
            row.Version++;
            row.UpdatedAt = _clock.GetUtcNow();
            return Task.FromResult(Snapshot(row));
        }
    }

    private Row Leased(Guid workItemId, Guid leaseToken, int expectedVersion, string operation)
    {
        if (!_rows.TryGetValue(workItemId, out var row)
            || row.Status != "leased"
            || row.LeaseToken != leaseToken
            || row.Version != expectedVersion
            || row.LeasedUntil <= _clock.GetUtcNow())
            throw new StateWorkConflictException(operation);

        row.Version++;
        row.UpdatedAt = _clock.GetUtcNow();
        return row;
    }

    private static void ClearLease(Row row)
    {
        row.LeaseToken = null;
        row.LeasedBy = null;
        row.LeasedUntil = null;
    }

    private static StateWorkItem Snapshot(Row row) => new()
    {
        WorkItemId = row.WorkItemId,
        Application = row.Application,
        Kind = row.Kind,
        SubjectRef = row.SubjectRef,
        Status = row.Status,
        Payload = row.Payload,
        Result = row.Result,
        ArtifactRef = row.ArtifactRef,
        ArtifactExpiresAt = row.ArtifactExpiresAt,
        DueAt = row.DueAt,
        Attempts = row.Attempts,
        MaxAttempts = row.MaxAttempts,
        Version = row.Version,
        LeaseToken = row.LeaseToken,
        LeasedBy = row.LeasedBy,
        LeasedUntil = row.LeasedUntil,
        LastError = row.LastError,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt,
        CompletedAt = row.CompletedAt,
    };

    private sealed class Row
    {
        public Guid WorkItemId;
        public string Application = string.Empty;
        public string Kind = string.Empty;
        public string SubjectRef = string.Empty;
        public string Status = "queued";
        public JsonElement Payload;
        public JsonElement Result;
        public string? ArtifactRef;
        public DateTimeOffset? ArtifactExpiresAt;
        public string? DownloadTokenHash;
        public DateTimeOffset DueAt;
        public int Attempts;
        public int MaxAttempts = 10;
        public int Version;
        public Guid? LeaseToken;
        public string? LeasedBy;
        public DateTimeOffset? LeasedUntil;
        public string? LastError;
        public DateTimeOffset CreatedAt;
        public DateTimeOffset UpdatedAt;
        public DateTimeOffset? CompletedAt;
    }
}
