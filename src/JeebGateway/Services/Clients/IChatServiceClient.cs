using JeebGateway.Chat;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Gateway-side BFF facade over the GENERIC chat-service (Firestore-backed,
/// C#/.NET 8, <c>Services:Chat:BaseUrl</c>). The chat-service exposes only
/// generic member / channel / session / message primitives:
///   <list type="bullet">
///     <item><c>POST /api/members</c> — create a member, returns its id.</item>
///     <item><c>POST /api/channels</c> — create a channel, returns its id.</item>
///     <item><c>POST /api/channels/{channelId}/members</c> — join a member, returns a SESSION id.</item>
///     <item><c>POST /api/channels/{channelId}/messages</c> — post a message (needs a valid session), returns a message id.</item>
///     <item><c>GET  /api/channels/{channelId}/messages/{messageId}</c> — fetch a single message.</item>
///     <item><c>GET  /api/channels/{channelId}/summary</c> — channel summary incl. last message.</item>
///   </list>
///
/// All Jeeb-specific aggregation — deriving a deterministic 1:1 channel for a
/// sorted user pair, ensuring members/channel/session exist, and assembling a
/// conversation history — lives HERE in the gateway BFF. The chat-service is
/// never called on any product-specific chat route; that surface has been
/// removed from the shared service.
///
/// The named "chat" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:Chat:BaseUrl</c>) + the org-standard resilience
/// pipeline, so this facade never manages retry/timeout/circuit-breaker.
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IChatServiceClient
{
    /// <summary>
    /// Sends a text message from <paramref name="senderId"/> to
    /// <paramref name="otherUserId"/>. Ensures the deterministic 1:1 channel for
    /// the sorted user pair exists (creating members, channel and sessions on the
    /// generic chat-service as needed), then posts via
    /// <c>POST /api/channels/{channelId}/messages</c> with the sender's session.
    /// Returns the created <see cref="ChatMessageDto"/>.
    /// </summary>
    Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        string otherUserId,
        string? text,
        CancellationToken ct);

    /// <summary>
    /// Sends a full <see cref="SendMessageRequest"/> from <paramref name="senderId"/>.
    /// Carries the message <see cref="ChatMessageType"/> and its type-specific
    /// payload (media URL, coordinates, offer id) so the BFF facade can validate
    /// the combination and persist/echo the correct discriminator — the text-only
    /// <see cref="SendMessageAsync(string,string,string?,CancellationToken)"/>
    /// overload loses everything but the text body.
    ///
    /// Additive (non-breaking): the production
    /// <see cref="ChatServiceClient"/> implements this by projecting onto the
    /// generic text-only upstream send, preserving existing behaviour; richer
    /// in-memory doubles (tests) can honour every field.
    /// </summary>
    Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        SendMessageRequest request,
        CancellationToken ct);

    /// <summary>
    /// Fetches the <paramref name="limit"/> most-recent messages in the
    /// conversation between <paramref name="userId"/> and
    /// <paramref name="otherUserId"/>. Resolves the deterministic channel for the
    /// pair and pages the generic chat-service list-messages endpoint
    /// (<c>GET /api/channels/{channelId}/messages</c>, newest-first cursor
    /// pagination) until <paramref name="limit"/> messages are gathered or the
    /// channel is exhausted. Returned oldest-first so callers can render a
    /// chronological transcript directly. Returns an empty list when no
    /// channel/history exists yet.
    /// </summary>
    Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Reads one newest-first page of messages for a resolved generic
    /// <paramref name="channelId"/> via the GENERIC chat-service endpoint
    /// <c>GET /api/channels/{channelId}/messages?limit={limit}&amp;before={beforeMessageId?}</c>.
    /// This is the raw cursor primitive the new generic list-messages endpoint
    /// exposes; <see cref="GetConversationAsync"/> and the dispute transcript
    /// helper compose it. <paramref name="beforeMessageId"/> is the
    /// <c>nextPageToken</c> from a prior page (null for the first page).
    /// Returns an empty page (no items, null token) when the channel has no
    /// messages or does not exist.
    /// </summary>
    Task<ChannelMessagePage> GetChannelMessagesAsync(
        string channelId,
        int limit,
        string? beforeMessageId,
        CancellationToken ct);

    /// <summary>
    /// Captures up to <paramref name="limit"/> messages of the conversation
    /// between <paramref name="userId"/> and <paramref name="otherUserId"/> for
    /// dispute evidence, oldest-first. Identical resolution to
    /// <see cref="GetConversationAsync"/>; named distinctly so the dispute
    /// orchestrator's intent (transcript capture) is explicit at the call site.
    /// </summary>
    Task<IReadOnlyList<ChatMessageDto>> GetConversationTranscriptAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct);

    /// <summary>
    /// Marks <paramref name="messageId"/> read by <paramref name="readerId"/> in
    /// the conversation between <paramref name="readerId"/> and
    /// <paramref name="otherUserId"/>, via the generic chat-service
    /// <c>POST /api/channels/{channelId}/messages/{messageId}/seen</c> surface.
    /// Returns the resolved <see cref="ChatMessageDto"/> with <c>ReadAt</c> set,
    /// or null when the channel/message cannot be resolved (idempotent no-op,
    /// never an error) so a duplicate mark-read does not surface a 500.
    /// </summary>
    Task<ChatMessageDto?> MarkMessageSeenAsync(
        string readerId,
        string otherUserId,
        string messageId,
        CancellationToken ct);
}

/// <summary>
/// One newest-first page of a channel's messages as returned by the GENERIC
/// chat-service list endpoint. <see cref="NextPageToken"/> carries the cursor
/// (a message id) to pass as <c>before</c> for the next (older) page; null when
/// there are no older messages.
/// </summary>
public sealed class ChannelMessagePage
{
    public required IReadOnlyList<ChatMessageDto> Items { get; init; }
    public string? NextPageToken { get; init; }
    public int TotalCount { get; init; }

    public static ChannelMessagePage Empty { get; } = new()
    {
        Items = Array.Empty<ChatMessageDto>(),
        NextPageToken = null,
        TotalCount = 0
    };
}
