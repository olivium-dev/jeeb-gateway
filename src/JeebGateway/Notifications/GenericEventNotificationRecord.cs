using System.Text.Json.Serialization;

namespace JeebGateway.Notifications;

/// <summary>
/// Wire DTO for notification-service <c>POST /notifications/events</c> (GenericEventNotification).
/// That handler upserts with <c>$setOnInsert</c> on <c>notification_id</c>, so two gateway
/// producers minting the same <see cref="NotificationCorrelationId"/> collapse to one dispatch.
/// </summary>
public sealed record GenericEventNotificationRecord
{
    [JsonPropertyName("notification_id")]
    public required string NotificationCorrelationId { get; init; }

    [JsonPropertyName("receiver")]
    public required string Receiver { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }

    /// <summary>Opaque to the shared service — it stores the string and resolves no template.</summary>
    [JsonPropertyName("event_type")]
    public required string EventType { get; init; }

    /// <summary>The flat routing map, copied verbatim into the dispatched push payload.</summary>
    [JsonPropertyName("data")]
    public IReadOnlyDictionary<string, string> Data { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    [JsonPropertyName("sender")]
    public string Sender { get; init; } = "jeeb-gateway";

    [JsonPropertyName("locale")]
    public string Locale { get; init; } = JeebNotificationCatalog.DefaultLocale;
}

/// <summary>
/// Opaque <c>event_type</c> strings for push kinds that have NO notification-centre route.
/// Named <c>*EventType</c>, never <c>TemplateKey</c>: the guard test
/// <c>NoSilentClassifiedType_HasACentreWriteDto</c> reflects over <c>TemplateKey</c> constants
/// to find centre-write DTOs, and a generic envelope is not one.
/// </summary>
public static class JeebGenericEventTypes
{
    public const string ChatMessageEventType = "jeeb.chat_message";
    public const string NewRequestEventType = "jeeb.new_request";
    public const string RequestExpiringEventType = "jeeb.request_expiring";
    public const string OfferLostEventType = "jeeb.offer_lost";

    // The five categories migrated off the deleted in-gateway push stack.
    // "jeeb.dispute_resolved" is NOT reused: that is a centre TemplateKey with a closed payload.
    public const string DisputeUpdateEventType = "jeeb.dispute_update";
    public const string CancellationDecisionEventType = "jeeb.cancellation_decision";
    public const string AutoOfflineEventType = "jeeb.auto_offline";
    public const string NotificationDispatchEventType = "jeeb.notification_dispatch";
}
