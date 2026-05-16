using System.Text.Json.Serialization;

namespace JeebGateway.Chat;

/// <summary>
/// Inbound payload for ChatHub.SendMessage. Mirrors the optional-field
/// pattern on <see cref="ChatMessage"/> — clients populate only the
/// fields relevant to <see cref="Type"/>; the hub validates the
/// combination before persisting.
/// </summary>
public sealed class SendMessageRequest
{
    public string? RecipientId { get; init; }
    public ChatMessageType Type { get; init; }
    public string? Text { get; init; }
    public string? MediaUrl { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? OfferId { get; init; }
}

/// <summary>
/// Wire shape pushed to clients via the "ReceiveMessage" hub event and
/// returned by the REST history endpoint. Kept separate from the domain
/// record so the persisted column set can evolve without breaking the
/// hub contract.
/// </summary>
public sealed class ChatMessageDto
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("conversationId")] public string ConversationId { get; init; } = "";
    [JsonPropertyName("senderId")] public string SenderId { get; init; } = "";
    [JsonPropertyName("recipientId")] public string RecipientId { get; init; } = "";
    [JsonPropertyName("type")] public ChatMessageType Type { get; init; }
    [JsonPropertyName("text")] public string? Text { get; init; }
    [JsonPropertyName("mediaUrl")] public string? MediaUrl { get; init; }
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
    [JsonPropertyName("offerId")] public string? OfferId { get; init; }
    [JsonPropertyName("sentAt")] public DateTimeOffset SentAt { get; init; }
    [JsonPropertyName("readAt")] public DateTimeOffset? ReadAt { get; init; }

    public static ChatMessageDto From(ChatMessage m) => new()
    {
        Id = m.Id,
        ConversationId = m.ConversationId,
        SenderId = m.SenderId,
        RecipientId = m.RecipientId,
        Type = m.Type,
        Text = m.Text,
        MediaUrl = m.MediaUrl,
        Latitude = m.Latitude,
        Longitude = m.Longitude,
        OfferId = m.OfferId,
        SentAt = m.SentAt,
        ReadAt = m.ReadAt
    };
}

/// <summary>
/// Payload of the "ReadReceipt" hub event — pushed to the sender once
/// the recipient marks one of their messages read.
/// </summary>
public sealed class ReadReceiptDto
{
    [JsonPropertyName("messageId")] public string MessageId { get; init; } = "";
    [JsonPropertyName("conversationId")] public string ConversationId { get; init; } = "";
    [JsonPropertyName("readerId")] public string ReaderId { get; init; } = "";
    [JsonPropertyName("readAt")] public DateTimeOffset ReadAt { get; init; }
}

/// <summary>REST shim for MarkRead so non-hub clients (tests, scripts) can update receipts.</summary>
public sealed class MarkReadRequest
{
    public string MessageId { get; init; } = "";
}
