namespace JeebGateway.Chat;

/// <summary>
/// Thrown when an inbound chat payload fails per-type validation
/// (missing required field for the requested type, attempting to author
/// a System message as a user, etc). The hub maps it to HubException so
/// the SignalR client receives a typed error; the REST shim maps it to
/// 400 Problem+JSON.
/// </summary>
public sealed class ChatValidationException : Exception
{
    public ChatValidationException(string message) : base(message) { }
}
