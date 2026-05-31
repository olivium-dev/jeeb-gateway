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
    /// Fetches the <paramref name="limit"/> most-recent messages in the
    /// conversation between <paramref name="userId"/> and
    /// <paramref name="otherUserId"/>. Resolves the deterministic channel for the
    /// pair and reads its messages from the generic chat-service. Returns an empty
    /// list when no channel/history exists yet.
    /// </summary>
    Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct);
}
