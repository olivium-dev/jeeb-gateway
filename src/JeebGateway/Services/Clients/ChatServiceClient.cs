using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using JeebGateway.Chat;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IChatServiceClient"/>.
/// Hand-coded against the Jeeb BFF surface on chat-api (Firestore-backed,
/// C#/.NET 8, base <c>Services:Chat:BaseUrl</c> = http://192.168.2.50:10028):
///   POST /api/jeeb/chat/messages                         — send (201)
///   GET  /api/jeeb/chat/conversations/{userId}/messages  — history (200)
///
/// The named "chat" HttpClient registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/> supplies
/// BaseAddress + the org-standard resilience pipeline (retry, circuit breaker,
/// timeout), so this class never manages retry/timeout/breaker directly.
///
/// The upstream returns camelCase JSON matching
/// <see cref="System.Text.Json.JsonSerializerDefaults.Web"/>.
/// </summary>
public sealed class ChatServiceClient : IChatServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public ChatServiceClient(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc/>
    public async Task<ChatMessageDto> SendMessageAsync(
        string senderId,
        string otherUserId,
        string? text,
        CancellationToken ct)
    {
        // POST /api/jeeb/chat/messages
        // Body: { "Text": "...", "OtherUserId": "..." }
        // Header: X-User-Id: <senderId>  (sender identity derived from JWT by gateway)
        var body = new WireSendRequest { Text = text, OtherUserId = otherUserId };

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/jeeb/chat/messages")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        request.Headers.Add("X-User-Id", senderId);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<WireChatMessage>(JsonOptions, ct)
            ?? throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");

        return wire.ToChatMessageDto();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ChatMessageDto>> GetConversationAsync(
        string userId,
        string otherUserId,
        int limit,
        CancellationToken ct)
    {
        // GET /api/jeeb/chat/conversations/{otherUserId}/messages?limit=N
        // Header: X-User-Id: <userId>
        var url = $"api/jeeb/chat/conversations/{Uri.EscapeDataString(otherUserId)}/messages?limit={limit}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-User-Id", userId);

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<List<WireChatMessage>>(JsonOptions, ct)
            ?? throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");

        return wire.Select(static m => m.ToChatMessageDto()).ToList().AsReadOnly();
    }

    // -------------------------------------------------------------------------
    // Wire DTOs — shapes as returned by chat-api (camelCase)
    // -------------------------------------------------------------------------

    private sealed class WireSendRequest
    {
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("otherUserId")] public string? OtherUserId { get; init; }
    }

    private sealed class WireChatMessage
    {
        [JsonPropertyName("id")] public string Id { get; init; } = "";
        [JsonPropertyName("conversationId")] public string ConversationId { get; init; } = "";
        [JsonPropertyName("senderId")] public string SenderId { get; init; } = "";
        [JsonPropertyName("recipientId")] public string RecipientId { get; init; } = "";
        [JsonPropertyName("type")] public string Type { get; init; } = "text";
        [JsonPropertyName("text")] public string? Text { get; init; }
        [JsonPropertyName("mediaUrl")] public string? MediaUrl { get; init; }
        [JsonPropertyName("latitude")] public double? Latitude { get; init; }
        [JsonPropertyName("longitude")] public double? Longitude { get; init; }
        [JsonPropertyName("offerId")] public string? OfferId { get; init; }
        [JsonPropertyName("sentAt")] public DateTimeOffset SentAt { get; init; }
        [JsonPropertyName("readAt")] public DateTimeOffset? ReadAt { get; init; }

        public ChatMessageDto ToChatMessageDto() => new()
        {
            Id = Id,
            ConversationId = ConversationId,
            SenderId = SenderId,
            RecipientId = RecipientId,
            Type = Enum.TryParse<ChatMessageType>(Type, ignoreCase: true, out var t)
                ? t
                : ChatMessageType.Text,
            Text = Text,
            MediaUrl = MediaUrl,
            Latitude = Latitude,
            Longitude = Longitude,
            OfferId = OfferId,
            SentAt = SentAt,
            ReadAt = ReadAt
        };
    }
}
