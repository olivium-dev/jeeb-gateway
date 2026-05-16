namespace JeebGateway.Push;

/// <summary>
/// Holds notifications that failed their first send attempt and are due for
/// a single retry 30 seconds later (T-backend-022 AC). The processor scans
/// this queue on a fixed cadence and fires <see cref="IPushNotificationService"/>
/// for each due entry without re-queuing — the retry policy is "once, then
/// drop to a hard failure".
/// </summary>
public interface IPushRetryQueue
{
    Task EnqueueAsync(PushRetryEntry entry, CancellationToken ct);
    Task<IReadOnlyList<PushRetryEntry>> DrainDueAsync(DateTimeOffset now, CancellationToken ct);
    int PendingCount { get; }
}

public sealed record PushRetryEntry(
    PushNotificationRequest Request,
    DateTimeOffset DueAt,
    string FailureReason);
