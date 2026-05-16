using System.Text.Json.Serialization;

namespace JeebGateway.Chat;

/// <summary>
/// The set of message payload shapes supported by the chat service
/// (T-backend-012). Each value selects which optional fields on
/// <see cref="ChatMessage"/> are populated. The send-time validator in
/// <see cref="ChatHub"/> enforces that the right fields are present for
/// the requested type and rejects malformed payloads before they hit the
/// store. Serialised as its string name so both REST clients and the
/// SignalR JSON protocol can round-trip without learning the enum's
/// underlying ordinal.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ChatMessageType
{
    /// <summary>Plain UTF-8 text body in <see cref="ChatMessage.Text"/>.</summary>
    Text,

    /// <summary>HTTPS URL of an uploaded image in <see cref="ChatMessage.MediaUrl"/>.</summary>
    ImageUrl,

    /// <summary>HTTPS URL of an uploaded voice note in <see cref="ChatMessage.MediaUrl"/>.</summary>
    VoiceNoteUrl,

    /// <summary>WGS-84 coordinates in <see cref="ChatMessage.Latitude"/> + <see cref="ChatMessage.Longitude"/>.</summary>
    Location,

    /// <summary>
    /// Server-emitted notice (e.g. "Delivery accepted"). System messages are
    /// authored by the gateway, never by a user — clients sending this type
    /// are rejected.
    /// </summary>
    System,

    /// <summary>
    /// Structured offer card referenced by <see cref="ChatMessage.OfferId"/>
    /// with a short preview in <see cref="ChatMessage.Text"/>.
    /// </summary>
    OfferCard
}
