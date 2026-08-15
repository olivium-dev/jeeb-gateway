using JeebGateway.Infrastructure;
using JeebGateway.Conversations.Client;

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
/// Additive chat-service export-index contract required to discover every
/// conversation visible to one user before paging the already-existing bounded
/// per-conversation export route.
///
/// GET /api/conversations/export-index
///     ?viewer={userId}&amp;asOf={stableInstant?}&amp;cursor={opaque?}&amp;limit={1..100}
///
/// The owner response pins <see cref="ChatConversationExportIndexPage.AsOf"/> on
/// the first page; callers send that exact value on every subsequent index and
/// message page so one export cannot drift while conversations are changing.
/// </summary>
public interface IChatConversationExportIndex
{
    Task<ChatConversationExportIndexPage> ListForViewerAsync(
        string viewerUserId,
        DateTimeOffset? asOf,
        string? cursor,
        int limit,
        CancellationToken ct);
}

public sealed record ChatConversationExportIndexPage(
    string ViewerId,
    DateTimeOffset AsOf,
    int Limit,
    bool HasMore,
    string? NextCursor,
    IReadOnlyList<string> ConversationIds);

/// <summary>
/// Explicit capability gate used until chat-service deploys export-index. It is
/// stateless and cannot fabricate an empty index.
/// </summary>
public sealed class UnavailableChatConversationExportIndex : IChatConversationExportIndex
{
    public Task<ChatConversationExportIndexPage> ListForViewerAsync(
        string viewerUserId,
        DateTimeOffset? asOf,
        string? cursor,
        int limit,
        CancellationToken ct) =>
        Task.FromException<ChatConversationExportIndexPage>(
            new OwnerCapabilityUnavailableException(
                "chat-service GET /api/conversations/export-index?viewer=&asOf=&cursor=&limit="));
}

/// <summary>
/// Live adapter for chat-service's additive export-index route. A 404/501 means
/// the owner deployment does not have the capability yet and is surfaced as an
/// explicit durable defer by <see cref="JeebGateway.Jobs.DataExportWorkHandler"/>.
/// </summary>
public sealed class ChatServiceConversationExportIndex(
    IJeebConversationClient owner) : IChatConversationExportIndex
{
    public async Task<ChatConversationExportIndexPage> ListForViewerAsync(
        string viewerUserId,
        DateTimeOffset? asOf,
        string? cursor,
        int limit,
        CancellationToken ct)
    {
        JeebConversationExportIndexPage page;
        try
        {
            page = await owner.ListConversationExportIndexAsync(
                viewerUserId, asOf, cursor, limit, ct);
        }
        catch (JeebConversationApiException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.NotFound
                or System.Net.HttpStatusCode.NotImplemented)
        {
            throw new OwnerCapabilityUnavailableException(
                "chat-service GET /api/conversations/export-index?viewer=&asOf=&cursor=&limit=");
        }

        return new ChatConversationExportIndexPage(
            page.ViewerId,
            page.AsOf,
            page.Limit,
            page.HasMore,
            page.NextCursor,
            page.ConversationIds.ToArray());
    }
}

/// <summary>
/// Default chat-history provider for GDPR export.
///
/// The generic chat-service contract currently has no "list channels for a
/// member" operation. Returning an empty transcript would create an incomplete
/// GDPR export that looked successful, so this adapter fails with the standard
/// owner-capability exception. The durable executor records an explicit defer
/// and retries until chat-service publishes the required owner operation (or the
/// export reaches its SLA and fails visibly).
/// </summary>
public sealed class ChatServiceDataExportChatHistoryProvider(
    IChatConversationExportIndex index,
    IJeebConversationClient conversations) : IDataExportChatHistoryProvider
{
    private const int IndexPageSize = 100;
    private const int MessagePageSize = 500;

    public async Task<IReadOnlyList<ChatMessageSnapshot>> GetForUserAsync(
        string userId,
        CancellationToken ct)
    {
        var conversationIds = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? asOf = null;
        string? cursor = null;
        do
        {
            var page = await index.ListForViewerAsync(
                userId, asOf, cursor, IndexPageSize, ct);
            if (!string.Equals(page.ViewerId, userId, StringComparison.Ordinal)
                || (asOf is not null && page.AsOf != asOf))
            {
                throw new InvalidDataException(
                    "chat-service export-index changed viewer or as_of during a data export");
            }
            asOf ??= page.AsOf;
            foreach (var conversationId in page.ConversationIds)
            {
                if (!string.IsNullOrWhiteSpace(conversationId))
                    conversationIds.Add(conversationId);
            }

            var next = page.HasMore ? page.NextCursor : null;
            if (page.HasMore && string.IsNullOrWhiteSpace(next))
                throw new InvalidDataException(
                    "chat-service export-index reported has_more without next_cursor");
            if (string.Equals(next, cursor, StringComparison.Ordinal))
                throw new InvalidDataException(
                    "chat-service export-index repeated its cursor");
            cursor = next;
        }
        while (cursor is not null);

        if (asOf is null)
            throw new InvalidDataException("chat-service export-index returned no stable as_of");

        var result = new List<ChatMessageSnapshot>();
        foreach (var conversationId in conversationIds.Order(StringComparer.Ordinal))
        {
            cursor = null;
            do
            {
                var page = await conversations.ExportMessagesForViewerAsync(
                    conversationId,
                    userId,
                    asOf,
                    cursor,
                    MessagePageSize,
                    ct);
                if (!string.Equals(page.ConversationId, conversationId, StringComparison.Ordinal)
                    || !string.Equals(page.ViewerId, userId, StringComparison.Ordinal)
                    || page.AsOf != asOf)
                {
                    throw new InvalidDataException(
                        "chat-service conversation export changed conversation, viewer, or as_of");
                }

                foreach (var message in page.Messages)
                {
                    if (message.CreatedAt is not { } createdAt)
                        throw new InvalidDataException(
                            "chat-service conversation export returned a message without created_at");
                    result.Add(new ChatMessageSnapshot
                    {
                        ConversationId = conversationId,
                        MessageId = message.MessageId,
                        SenderId = message.AuthorId ?? string.Empty,
                        Body = message.Body
                               ?? message.Payload?.GetRawText()
                               ?? string.Empty,
                        SentAt = new DateTimeOffset(createdAt),
                    });
                }

                var next = page.HasMore ? page.NextCursor : null;
                if (page.HasMore && string.IsNullOrWhiteSpace(next))
                    throw new InvalidDataException(
                        "chat-service conversation export reported has_more without next_cursor");
                if (string.Equals(next, cursor, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "chat-service conversation export repeated its cursor");
                cursor = next;
            }
            while (cursor is not null);
        }

        return result.OrderBy(message => message.SentAt)
            .ThenBy(message => message.ConversationId, StringComparer.Ordinal)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .ToArray();
    }
}
