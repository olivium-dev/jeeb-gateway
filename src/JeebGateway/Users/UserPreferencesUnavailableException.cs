namespace JeebGateway.Users;

/// <summary>
/// The remote-user-preferences upstream could NOT answer a read (transport failure,
/// breaker-open, or read-budget expiry) — the value the store would otherwise return
/// is a placeholder (empty list / defaults), not the user's stored state.
/// Mirrors <c>WalletLedgerUnavailableException</c>: read paths surface this as a
/// retryable 502, never as an authoritative 200 (O10 precedent).
/// </summary>
public sealed class UserPreferencesUnavailableException : Exception
{
    public UserPreferencesUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
