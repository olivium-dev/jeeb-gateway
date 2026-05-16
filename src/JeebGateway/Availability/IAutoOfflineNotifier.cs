namespace JeebGateway.Availability;

/// <summary>
/// Push notification fan-out for auto-offline events. Production
/// implementation talks to notification-service; the in-memory variant
/// records calls so tests can assert delivery.
/// </summary>
public interface IAutoOfflineNotifier
{
    Task NotifyAutoOfflineAsync(string userId, DateTimeOffset at, CancellationToken ct);
}

public class InMemoryAutoOfflineNotifier : IAutoOfflineNotifier
{
    private readonly List<(string UserId, DateTimeOffset At)> _sent = new();
    private readonly object _lock = new();

    public Task NotifyAutoOfflineAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_lock) _sent.Add((userId, at));
        return Task.CompletedTask;
    }

    public IReadOnlyList<(string UserId, DateTimeOffset At)> Sent
    {
        get { lock (_lock) return _sent.ToArray(); }
    }
}
