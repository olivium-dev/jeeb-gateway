namespace JeebGateway.Chat;

/// <summary>
/// Coordinates the four side effects of a chat message — validate +
/// persist, deliver live via the hub, push-fallback when the recipient
/// is backgrounded, and broadcast read receipts. Keeps the hub thin and
/// makes the same path available to the REST shim without round-tripping
/// through SignalR.
/// </summary>
public interface IChatDispatcher
{
    /// <summary>
    /// Validates the payload, persists it, fans out to live recipients
    /// and triggers the push stub when the recipient is backgrounded.
    /// Returns the persisted message so the caller can echo it.
    /// </summary>
    Task<ChatMessage> SendAsync(
        string senderId,
        SendMessageRequest request,
        CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="messageId"/> as read by <paramref name="readerId"/>
    /// and pushes a "ReadReceipt" event to the original sender. Returns
    /// null if the message does not exist or the reader is not the
    /// recipient (idempotent no-op rather than error).
    /// </summary>
    Task<ChatMessage?> MarkReadAsync(
        string messageId,
        string readerId,
        CancellationToken ct);
}
