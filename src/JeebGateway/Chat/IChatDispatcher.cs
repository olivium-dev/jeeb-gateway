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
    /// in the conversation with <paramref name="otherUserId"/>, persisting the
    /// receipt on the generic chat-service via <see cref="Services.Clients.IChatServiceClient"/>
    /// and pushing a "ReadReceipt" event to the conversation group. Returns null
    /// if the message/channel cannot be resolved (idempotent no-op rather than
    /// error).
    ///
    /// Read receipts are conversation-scoped: the generic upstream addresses
    /// messages by (channelId, messageId), and the gateway resolves the channel
    /// from the sorted (reader, other) pair — so the counterpart id is required.
    /// </summary>
    Task<ChatMessage?> MarkReadAsync(
        string otherUserId,
        string messageId,
        string readerId,
        CancellationToken ct);
}
