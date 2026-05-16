namespace JeebGateway.Chat;

/// <summary>
/// A single persisted chat message in a conversation between two users.
/// One conversation id is shared by both participants and is canonicalised
/// as the lexicographically-sorted pair "min|max" so either participant
/// can derive it without server lookup (see <see cref="ConversationKey"/>).
///
/// Optional payload fields are populated based on <see cref="Type"/>:
///   - Text                → <see cref="Text"/>
///   - ImageUrl / VoiceNoteUrl → <see cref="MediaUrl"/>
///   - Location            → <see cref="Latitude"/> + <see cref="Longitude"/>
///   - System              → <see cref="Text"/> (server-authored)
///   - OfferCard           → <see cref="OfferId"/> + <see cref="Text"/> preview
/// </summary>
public sealed record ChatMessage
{
    public required string Id { get; init; }
    public required string ConversationId { get; init; }
    public required string SenderId { get; init; }
    public required string RecipientId { get; init; }
    public required ChatMessageType Type { get; init; }
    public required DateTimeOffset SentAt { get; init; }

    public string? Text { get; init; }
    public string? MediaUrl { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? OfferId { get; init; }

    /// <summary>
    /// Set when the recipient has acknowledged the message via the read
    /// receipt path (hub MarkRead or the REST shim). Null while unread.
    /// </summary>
    public DateTimeOffset? ReadAt { get; init; }
}
