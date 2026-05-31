using JeebGateway.Chat;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real chat-api (Firestore-backed, C#/.NET 8).
/// Hand-coded against the Jeeb BFF surface exposed by chat-api:
///   POST /api/jeeb/chat/messages          — send a message (201)
///   GET  /api/jeeb/chat/conversations/{otherUserId}/messages?limit=N — history
///
/// Both endpoints derive the sender identity from the <c>X-User-Id</c> header
/// rather than the request body, matching the BFF trust model (gateway extracts
/// userId from the validated JWT and forwards it as a trusted header).
///
/// The named "chat" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress (<c>Services:Chat:BaseUrl</c>) + the org-standard resilience
/// pipeline, so this class never has to manage retry/timeout/circuit-breaker.
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IChatServiceClient
{
    /// <summary>
    /// Sends a message to <paramref name="otherUserId"/> on behalf of
    /// <paramref name="senderId"/>. Proxies <c>POST /api/jeeb/chat/messages</c>.
    /// Returns the created <see cref="ChatMessageDto"/> on 201.
    /// </summary>
    Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        string otherUserId,
        string? text,
        CancellationToken ct);

    /// <summary>
    /// Fetches the <paramref name="limit"/> most-recent messages in the
    /// conversation between <paramref name="userId"/> and <paramref name="otherUserId"/>.
    /// Proxies <c>GET /api/jeeb/chat/conversations/{otherUserId}/messages?limit={limit}</c>.
    /// </summary>
    Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct);
}
