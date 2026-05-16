using System.Collections.Concurrent;

namespace JeebGateway.Chat;

/// <summary>
/// In-memory presence tracker. Keyed by user, value is the set of
/// (connectionId → isForeground) pairs the user is currently holding.
/// Default state for a new connection is foreground=true; the client
/// flips it on app background via SetForegroundState.
/// </summary>
public sealed class InMemoryChatPresenceTracker : IChatPresenceTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _users = new();

    public void Connect(string userId, string connectionId)
    {
        var conns = _users.GetOrAdd(userId, _ => new ConcurrentDictionary<string, bool>());
        conns[connectionId] = true;
    }

    public void Disconnect(string userId, string connectionId)
    {
        if (!_users.TryGetValue(userId, out var conns)) return;
        conns.TryRemove(connectionId, out _);
        if (conns.IsEmpty)
        {
            _users.TryRemove(userId, out _);
        }
    }

    public void SetForegroundState(string userId, string connectionId, bool isForeground)
    {
        if (!_users.TryGetValue(userId, out var conns)) return;
        conns[connectionId] = isForeground;
    }

    public bool IsForegrounded(string userId)
    {
        return _users.TryGetValue(userId, out var conns)
            && conns.Values.Any(v => v);
    }
}
