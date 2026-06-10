namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Outbox store for notification dispatch jobs.
/// Provides idempotency checking, retry scheduling, and DLQ support.
/// </summary>
public interface INotificationDispatchOutbox
{
    /// <summary>Returns true if an entry with this idempotency key already exists.</summary>
    Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Adds a new entry to the outbox.</summary>
    Task<NotificationDispatchEntry> AddAsync(NotificationDispatchEntry entry, CancellationToken ct = default);

    /// <summary>Returns all entries that are due for processing (Pending and NextAttemptAt &lt;= now).</summary>
    Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default);

    /// <summary>Marks an entry as successfully delivered.</summary>
    Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default);

    /// <summary>Schedules a retry for a failed entry, or moves it to DLQ after max attempts.</summary>
    Task RecordFailureAsync(Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default);

    /// <summary>Returns all DLQ entries for observability.</summary>
    Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default);

    /// <summary>Total count of pending entries (for diagnostics).</summary>
    int PendingCount { get; }
}
