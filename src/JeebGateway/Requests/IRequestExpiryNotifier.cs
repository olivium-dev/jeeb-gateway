namespace JeebGateway.Requests;

/// <summary>
/// Push fan-out for the two expiry-window events (T-backend-028).
/// Production wiring proxies to notification-service via the BFF
/// NSwag-generated client; the in-memory variant records calls so
/// integration tests can assert delivery and ordering.
/// </summary>
public interface IRequestExpiryNotifier
{
    /// <summary>
    /// Fired once per request when the no-offer window elapses with the
    /// request still in <c>pending</c>. Mobile renders the "Try expanding
    /// tier" prompt with a re-request CTA.
    /// </summary>
    Task NotifyTryExpandTierAsync(string clientId, string requestId, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Fired when a request is terminally expired (no accepted offer
    /// inside <see cref="RequestExpiryOptions.ExpiryWindow"/>). Mobile
    /// renders the "Request expired — tap to re-request" notification.
    /// </summary>
    Task NotifyExpiredAsync(string clientId, string requestId, DateTimeOffset at, CancellationToken ct);
}

public class InMemoryRequestExpiryNotifier : IRequestExpiryNotifier
{
    private readonly List<NudgeRecord> _nudges = new();
    private readonly List<ExpiryRecord> _expiries = new();
    private readonly object _lock = new();

    public Task NotifyTryExpandTierAsync(string clientId, string requestId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_lock) _nudges.Add(new NudgeRecord(clientId, requestId, at));
        return Task.CompletedTask;
    }

    public Task NotifyExpiredAsync(string clientId, string requestId, DateTimeOffset at, CancellationToken ct)
    {
        lock (_lock) _expiries.Add(new ExpiryRecord(clientId, requestId, at));
        return Task.CompletedTask;
    }

    public IReadOnlyList<NudgeRecord> Nudges
    {
        get { lock (_lock) return _nudges.ToArray(); }
    }

    public IReadOnlyList<ExpiryRecord> Expiries
    {
        get { lock (_lock) return _expiries.ToArray(); }
    }

    public sealed record NudgeRecord(string ClientId, string RequestId, DateTimeOffset At);
    public sealed record ExpiryRecord(string ClientId, string RequestId, DateTimeOffset At);
}
