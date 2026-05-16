namespace JeebGateway.Chat;

/// <summary>
/// Tracks whether a user's app is in the foreground (connected to a hub
/// AND not explicitly backgrounded) so the message dispatcher can route
/// to either the live WS channel or the push notification stub. AC for
/// T-backend-012 line 5: "If recipient app is backgrounded, trigger push
/// notification stub."
///
/// The tracker is intentionally per-user (not per-connection) — a user
/// can have one app on phone + one on tablet; if either is foregrounded
/// we deliver live. Backgrounded state is reported by the client through
/// the hub's SetForegroundState method and clears on disconnect.
/// </summary>
public interface IChatPresenceTracker
{
    /// <summary>Records that <paramref name="connectionId"/> is an active hub connection for <paramref name="userId"/>.</summary>
    void Connect(string userId, string connectionId);

    /// <summary>Releases <paramref name="connectionId"/>; the user is offline when their last connection drops.</summary>
    void Disconnect(string userId, string connectionId);

    /// <summary>
    /// Updates the client-reported foreground/background state for the
    /// given connection. A user is considered foregrounded only if at
    /// least one of their connections reports IsForeground=true.
    /// </summary>
    void SetForegroundState(string userId, string connectionId, bool isForeground);

    /// <summary>
    /// True iff the user has at least one connection that is currently
    /// reporting foreground. False for disconnected users and for users
    /// whose every connection has called SetForegroundState(false).
    /// </summary>
    bool IsForegrounded(string userId);
}
