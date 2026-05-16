namespace JeebGateway.Requests;

/// <summary>
/// Push fan-out for the scheduled-delivery matching-window reminder
/// (T-backend-046). Production wiring proxies to notification-service via
/// the BFF NSwag-generated client; the in-memory variant records calls so
/// integration tests can assert delivery and ordering.
///
/// The Jeeber-side reminder ("you have a scheduled pickup in 30 minutes")
/// fires from the matching event downstream (offer-service emits a push
/// once a candidate pool is matched to the now-pending request). The
/// gateway activator only owns the Client-side reminder, fired the same
/// instant matching is triggered.
/// </summary>
public interface IScheduledDeliveryNotifier
{
    /// <summary>
    /// Fired once per scheduled request when the matching window opens
    /// (ScheduledAt - MatchingBuffer). Mobile renders "Your scheduled
    /// delivery starts matching now" with a tap-through to the request.
    /// </summary>
    Task NotifyClientMatchingWindowOpenedAsync(
        string clientId,
        string requestId,
        DateTimeOffset scheduledAt,
        DateTimeOffset at,
        CancellationToken ct);
}

/// <summary>
/// In-memory notifier — records calls in arrival order so integration
/// tests can assert which fan-out fired, when, and to whom. Production
/// swap implements this against notification-service.
/// </summary>
public class InMemoryScheduledDeliveryNotifier : IScheduledDeliveryNotifier
{
    private readonly List<ClientReminderRecord> _clientReminders = new();
    private readonly object _lock = new();

    public Task NotifyClientMatchingWindowOpenedAsync(
        string clientId,
        string requestId,
        DateTimeOffset scheduledAt,
        DateTimeOffset at,
        CancellationToken ct)
    {
        lock (_lock)
        {
            _clientReminders.Add(new ClientReminderRecord(clientId, requestId, scheduledAt, at));
        }
        return Task.CompletedTask;
    }

    public IReadOnlyList<ClientReminderRecord> ClientReminders
    {
        get { lock (_lock) return _clientReminders.ToArray(); }
    }

    public sealed record ClientReminderRecord(
        string ClientId,
        string RequestId,
        DateTimeOffset ScheduledAt,
        DateTimeOffset At);
}
