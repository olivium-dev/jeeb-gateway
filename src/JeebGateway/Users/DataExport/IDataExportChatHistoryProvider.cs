using System.Collections.Concurrent;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Seam for fetching the user's chat history at export time. Chat lives
/// in chat-service; the gateway calls it through this interface so the
/// packager doesn't need to know whether the data is local, hits an
/// NSwag-generated client, or is read out of a downstream Postgres
/// replica. The MVP backs it with <see cref="InMemoryDataExportChatHistoryProvider"/>.
/// </summary>
public interface IDataExportChatHistoryProvider
{
    Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(string userId, CancellationToken ct);
}

public class ChatMessageSnapshot
{
    public required string ConversationId { get; init; }
    public required string MessageId { get; init; }
    public required string SenderId { get; init; }
    public required string Body { get; init; }
    public required DateTimeOffset SentAt { get; init; }
}

/// <summary>
/// MVP stand-in. Tests seed messages via <see cref="Seed"/>; production
/// wiring replaces this with an NSwag-generated chat-service client.
/// </summary>
public class InMemoryDataExportChatHistoryProvider : IDataExportChatHistoryProvider
{
    private readonly ConcurrentDictionary<string, List<ChatMessageSnapshot>> _byUser = new();

    public Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        if (_byUser.TryGetValue(userId, out var list))
        {
            IReadOnlyList<ChatMessageSnapshot> snapshot = list.OrderBy(m => m.SentAt).ToArray();
            return Task.FromResult(snapshot);
        }
        return Task.FromResult<IReadOnlyList<ChatMessageSnapshot>>(Array.Empty<ChatMessageSnapshot>());
    }

    public void Seed(string userId, params ChatMessageSnapshot[] messages)
    {
        _byUser.AddOrUpdate(
            userId,
            _ => messages.ToList(),
            (_, existing) =>
            {
                existing.AddRange(messages);
                return existing;
            });
    }
}
