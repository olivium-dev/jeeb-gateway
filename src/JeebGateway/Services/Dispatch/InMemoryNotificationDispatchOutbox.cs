namespace JeebGateway.Services.Dispatch;

/// <summary>
/// In-memory implementation of the notification dispatch outbox (MVP).
/// Swap for a Postgres-backed implementation backed by the
/// <c>notification_dispatch_outbox</c> table when persistence is needed.
/// </summary>
public sealed class InMemoryNotificationDispatchOutbox : INotificationDispatchOutbox
{
    private readonly List<NotificationDispatchEntry> _entries = new();
    private readonly object _lock = new();

    public int PendingCount
    {
        get
        {
            lock (_lock) return _entries.Count(e => e.Status == NotificationDispatchStatus.Pending);
        }
    }

    public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult(
                _entries.Any(e => e.IdempotencyKey != null &&
                                  string.Equals(e.IdempotencyKey, idempotencyKey, StringComparison.Ordinal)));
        }
    }

    public Task<NotificationDispatchEntry> AddAsync(NotificationDispatchEntry entry, CancellationToken ct = default)
    {
        lock (_lock) _entries.Add(entry);
        return Task.FromResult(entry);
    }

    public Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var due = _entries
                .Where(e => e.Status == NotificationDispatchStatus.Pending &&
                            (e.NextAttemptAt == null || e.NextAttemptAt <= now))
                .ToList();
            return Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(due);
        }
    }

    public Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId);
            if (entry != null) entry.Status = NotificationDispatchStatus.Delivered;
        }
        return Task.CompletedTask;
    }

    public Task RecordFailureAsync(Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == entryId);
            if (entry == null) return Task.CompletedTask;

            entry.AttemptCount++;
            entry.LastError = error;

            if (entry.AttemptCount >= maxAttempts)
            {
                entry.Status = NotificationDispatchStatus.DLQ;
            }
            else
            {
                entry.NextAttemptAt = DateTimeOffset.UtcNow.Add(retryDelay);
            }
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            var dlq = _entries
                .Where(e => e.Status == NotificationDispatchStatus.DLQ)
                .ToList();
            return Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(dlq);
        }
    }
}
