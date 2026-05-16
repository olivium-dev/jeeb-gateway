using System.Collections.Concurrent;

namespace JeebGateway.Chat;

/// <summary>
/// In-memory chat message store for the MVP. Per-conversation
/// <see cref="List{T}"/> instances are guarded by a per-conversation lock
/// so two concurrent appends to the same conversation never tear the
/// chronological order. The id index is a <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// so MarkRead lookups don't block the append path.
/// </summary>
public sealed class InMemoryChatMessageStore : IChatMessageStore
{
    private readonly ConcurrentDictionary<string, List<ChatMessage>> _byConversation = new();
    private readonly ConcurrentDictionary<string, ChatMessage> _byId = new();
    private readonly object _lock = new();

    public Task AppendAsync(ChatMessage message, CancellationToken ct)
    {
        var bucket = _byConversation.GetOrAdd(message.ConversationId, _ => new List<ChatMessage>());
        lock (_lock)
        {
            bucket.Add(message);
            _byId[message.Id] = message;
        }
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ChatMessage>> GetByConversationAsync(
        string conversationId,
        int limit,
        CancellationToken ct)
    {
        if (limit <= 0) limit = 50;

        IReadOnlyList<ChatMessage> snapshot = Array.Empty<ChatMessage>();
        if (_byConversation.TryGetValue(conversationId, out var bucket))
        {
            lock (_lock)
            {
                snapshot = bucket
                    .OrderBy(m => m.SentAt)
                    .ThenBy(m => m.Id, StringComparer.Ordinal)
                    .TakeLast(limit)
                    .ToArray();
            }
        }
        return Task.FromResult(snapshot);
    }

    public Task<ChatMessage?> GetAsync(string messageId, CancellationToken ct)
    {
        _byId.TryGetValue(messageId, out var msg);
        return Task.FromResult<ChatMessage?>(msg);
    }

    public Task<ChatMessage?> MarkReadAsync(
        string messageId,
        string reader,
        DateTimeOffset readAt,
        CancellationToken ct)
    {
        if (!_byId.TryGetValue(messageId, out var existing)) return Task.FromResult<ChatMessage?>(null);
        if (!string.Equals(existing.RecipientId, reader, StringComparison.Ordinal)) return Task.FromResult<ChatMessage?>(null);
        if (existing.ReadAt.HasValue) return Task.FromResult<ChatMessage?>(existing);

        var updated = existing with { ReadAt = readAt };
        lock (_lock)
        {
            _byId[messageId] = updated;
            if (_byConversation.TryGetValue(existing.ConversationId, out var bucket))
            {
                var idx = bucket.FindIndex(m => m.Id == messageId);
                if (idx >= 0) bucket[idx] = updated;
            }
        }
        return Task.FromResult<ChatMessage?>(updated);
    }
}
