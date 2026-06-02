using Microsoft.Extensions.Logging;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Seam for fetching the user's chat history at export time. Chat lives in the
/// generic chat-service (now consumed only by the passthrough ChatController via
/// the NSwag ServiceChatClient); the gateway holds no chat record-of-truth and no
/// BFF chat client, so per-user conversation enumeration is a chat-service
/// concern. This seam stays so the export packager keeps a stable contract.
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
/// Default chat-history provider for GDPR export.
///
/// LIMITATION (documented, non-silent): the gateway no longer carries a chat BFF
/// client or an in-memory chat store — chat is owned end-to-end by the generic
/// chat-service, which exposes no "list channels for a member" endpoint. A
/// complete per-user export therefore awaits a generic list-channels-for-member
/// endpoint on chat-service; until then this provider returns an empty transcript
/// and logs a warning rather than silently claiming a complete export.
/// </summary>
public sealed class ChatServiceDataExportChatHistoryProvider : IDataExportChatHistoryProvider
{
    private readonly ILogger<ChatServiceDataExportChatHistoryProvider> _log;

    public ChatServiceDataExportChatHistoryProvider(
        ILogger<ChatServiceDataExportChatHistoryProvider> log)
    {
        _log = log;
    }

    public Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        _log.LogWarning(
            "Data export chat history for user {UserId} is empty: the gateway holds no chat " +
            "store and the generic chat-service exposes no list-channels-for-member endpoint, " +
            "so per-user conversation enumeration is not yet possible. Pending chat-service follow-up.",
            userId);
        return Task.FromResult<IReadOnlyList<ChatMessageSnapshot>>(Array.Empty<ChatMessageSnapshot>());
    }
}
