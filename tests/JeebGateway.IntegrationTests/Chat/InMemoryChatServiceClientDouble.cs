using System.Collections.Concurrent;
using JeebGateway.Chat;
using JeebGateway.Services.Clients;

namespace JeebGateway.IntegrationTests.Chat;

/// <summary>
/// Self-contained in-memory <see cref="IChatServiceClient"/> for tests. It is the
/// record-of-truth the production code deleted: the real
/// <see cref="ChatServiceClient"/> talks HTTP to the generic chat-service, which
/// is unreachable in the suite, so every test that exercises the chat send /
/// history / transcript / read-receipt paths swaps in this double.
///
/// It honours the FULL <see cref="SendMessageRequest"/> (type + type-specific
/// payload), persists per-conversation keyed by <see cref="ConversationKey"/>,
/// returns history oldest-first (matching the production client's contract), and
/// implements mark-seen so the dispatcher's read-receipt fan-out works end to end.
///
/// Registered as a singleton in tests so seeded state survives across the request
/// scopes the controller/dispatcher resolve from (the production client is
/// stateless, so lifetime is a test-only concern).
/// </summary>
public sealed class InMemoryChatServiceClientDouble : IChatServiceClient
{
    private readonly ConcurrentDictionary<string, List<ChatMessageDto>> _byConversation =
        new(StringComparer.Ordinal);
    private readonly object _lock = new();

    // Text-only legacy overload — projects onto the rich path.
    public Task<ChatMessageDto> SendMessageAsync(
        string senderId, string otherUserId, string? text, CancellationToken ct) =>
        SendMessageAsync(senderId, new SendMessageRequest
        {
            RecipientId = otherUserId,
            Type = ChatMessageType.Text,
            Text = text
        }, ct);

    public Task<ChatMessageDto> SendMessageAsync(
        string senderId, SendMessageRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.RecipientId))
            throw new ChatValidationException("recipientId is required");

        var conversationId = ConversationKey.For(senderId, request.RecipientId!);
        var dto = new ChatMessageDto
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            SenderId = senderId,
            RecipientId = request.RecipientId!,
            Type = request.Type,
            Text = request.Text,
            MediaUrl = request.MediaUrl,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            OfferId = request.OfferId,
            SentAt = DateTimeOffset.UtcNow
        };

        Append(dto);
        return Task.FromResult(dto);
    }

    public Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId, string otherUserId, int limit, CancellationToken ct) =>
        Task.FromResult(Read(userId, otherUserId, limit));

    public Task<IReadOnlyList<ChatMessageDto>> GetConversationTranscriptAsync(
        string userId, string otherUserId, int limit, CancellationToken ct) =>
        Task.FromResult(Read(userId, otherUserId, limit));

    public Task<ChannelMessagePage> GetChannelMessagesAsync(
        string channelId, int limit, string? beforeMessageId, CancellationToken ct)
    {
        // channelId here is the ConversationKey (the double keys by it directly).
        lock (_lock)
        {
            if (!_byConversation.TryGetValue(channelId, out var bucket) || bucket.Count == 0)
                return Task.FromResult(ChannelMessagePage.Empty);

            // Newest-first, like the generic endpoint.
            var ordered = bucket.OrderByDescending(m => m.SentAt).ThenByDescending(m => m.Id).ToList();
            var startIndex = 0;
            if (!string.IsNullOrEmpty(beforeMessageId))
            {
                var cursor = ordered.FindIndex(m => m.Id == beforeMessageId);
                startIndex = cursor >= 0 ? cursor + 1 : ordered.Count;
            }

            var page = ordered.Skip(startIndex).Take(limit).ToList();
            string? next = (startIndex + page.Count) < ordered.Count && page.Count > 0
                ? page[^1].Id
                : null;

            return Task.FromResult(new ChannelMessagePage
            {
                Items = page,
                NextPageToken = next,
                TotalCount = ordered.Count
            });
        }
    }

    public Task<ChatMessageDto?> MarkMessageSeenAsync(
        string readerId, string otherUserId, string messageId, CancellationToken ct)
    {
        var conversationId = ConversationKey.For(readerId, otherUserId);
        lock (_lock)
        {
            if (!_byConversation.TryGetValue(conversationId, out var bucket))
                return Task.FromResult<ChatMessageDto?>(null);

            var idx = bucket.FindIndex(m => m.Id == messageId);
            if (idx < 0) return Task.FromResult<ChatMessageDto?>(null);

            var existing = bucket[idx];
            // Only the recipient marks read; race-y duplicate is a no-op.
            if (!string.Equals(existing.RecipientId, readerId, StringComparison.Ordinal))
                return Task.FromResult<ChatMessageDto?>(null);

            var updated = new ChatMessageDto
            {
                Id = existing.Id,
                ConversationId = existing.ConversationId,
                SenderId = existing.SenderId,
                RecipientId = existing.RecipientId,
                Type = existing.Type,
                Text = existing.Text,
                MediaUrl = existing.MediaUrl,
                Latitude = existing.Latitude,
                Longitude = existing.Longitude,
                OfferId = existing.OfferId,
                SentAt = existing.SentAt,
                ReadAt = existing.ReadAt ?? DateTimeOffset.UtcNow
            };
            bucket[idx] = updated;
            return Task.FromResult<ChatMessageDto?>(updated);
        }
    }

    /// <summary>
    /// Test seam: seed a message directly (used by dispute-evidence transcript
    /// tests and the system-message-authored-by-dispatcher test).
    /// </summary>
    public void Seed(ChatMessageDto dto) => Append(dto);

    private void Append(ChatMessageDto dto)
    {
        lock (_lock)
        {
            var bucket = _byConversation.GetOrAdd(dto.ConversationId, _ => new List<ChatMessageDto>());
            bucket.Add(dto);
        }
    }

    private IReadOnlyList<ChatMessageDto> Read(string userId, string otherUserId, int limit)
    {
        var conversationId = ConversationKey.For(userId, otherUserId);
        lock (_lock)
        {
            if (!_byConversation.TryGetValue(conversationId, out var bucket))
                return Array.Empty<ChatMessageDto>();

            if (limit <= 0) limit = 50;
            // Oldest-first, capped at limit (most-recent `limit` messages).
            return bucket
                .OrderBy(m => m.SentAt)
                .ThenBy(m => m.Id, StringComparer.Ordinal)
                .TakeLast(limit)
                .ToList();
        }
    }
}
