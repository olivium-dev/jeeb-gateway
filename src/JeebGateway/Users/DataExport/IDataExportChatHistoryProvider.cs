using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Seam for fetching the user's chat history at export time. Chat lives in the
/// generic chat-service; the gateway reads it through the BFF
/// <see cref="IChatServiceClient"/> so the packager doesn't need to know the
/// upstream topology. The in-memory record-of-truth has been removed — retention
/// and storage are chat-service concerns now.
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
/// Reads a user's chat history for GDPR export through the generic chat-service
/// via <see cref="IChatServiceClient"/> — the gateway holds no in-memory chat
/// store.
///
/// LIMITATION (documented, non-silent): the GENERIC chat-service exposes no
/// "list channels for a member" endpoint (only per-channel reads), so the gateway
/// cannot enumerate every conversation a user participated in without a
/// per-user channel index. Until chat-service adds a generic
/// list-channels-for-member endpoint (chat-service follow-up), this provider
/// returns an empty transcript and logs a warning rather than silently claiming a
/// complete export. The DI seam stays on the client so the per-channel read path
/// is already wired the moment that endpoint lands.
/// </summary>
public sealed class ChatServiceDataExportChatHistoryProvider : IDataExportChatHistoryProvider
{
    private readonly IChatServiceClient _chat;
    private readonly ILogger<ChatServiceDataExportChatHistoryProvider> _log;

    public ChatServiceDataExportChatHistoryProvider(
        IChatServiceClient chat,
        ILogger<ChatServiceDataExportChatHistoryProvider> log)
    {
        _chat = chat;
        _log = log;
    }

    public Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        // The generic chat-service can read messages for a known channel, but it
        // cannot enumerate the channels a member belongs to. A complete per-user
        // export therefore awaits a generic list-channels-for-member endpoint;
        // until then we return empty and surface the gap in logs (never a silent
        // partial export). Reference _chat to keep the client dependency wired for
        // the moment that endpoint lands.
        _ = _chat;
        _log.LogWarning(
            "Data export chat history for user {UserId} is empty: the generic chat-service " +
            "exposes no list-channels-for-member endpoint, so per-user conversation enumeration " +
            "is not yet possible. Pending chat-service follow-up.",
            userId);
        return Task.FromResult<IReadOnlyList<ChatMessageSnapshot>>(Array.Empty<ChatMessageSnapshot>());
    }
}
