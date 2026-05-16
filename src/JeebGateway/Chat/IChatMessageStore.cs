namespace JeebGateway.Chat;

/// <summary>
/// Persistence surface for chat messages (T-backend-012). In-memory for
/// the MVP — the production swap targets a Postgres table keyed by
/// (conversation_id, sent_at) with the same shape as <see cref="ChatMessage"/>,
/// proxied through an NSwag-generated chat-service client per the BFF
/// aggregation policy.
/// </summary>
public interface IChatMessageStore
{
    Task AppendAsync(ChatMessage message, CancellationToken ct);

    Task<IReadOnlyList<ChatMessage>> GetByConversationAsync(
        string conversationId,
        int limit,
        CancellationToken ct);

    Task<ChatMessage?> GetAsync(string messageId, CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="messageId"/> as read at <paramref name="readAt"/>
    /// when <paramref name="reader"/> is the recipient. Returns the updated
    /// message, or null if the message does not exist or the caller is not
    /// the recipient (which we treat as a no-op rather than an error so a
    /// duplicate MarkRead from the client does not 500).
    /// </summary>
    Task<ChatMessage?> MarkReadAsync(
        string messageId,
        string reader,
        DateTimeOffset readAt,
        CancellationToken ct);
}
