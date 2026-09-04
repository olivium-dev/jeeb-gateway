using System.Text.Json.Serialization;

namespace JeebGateway.Notifications;

/// <summary>
/// Authoritative values already present at the offer-submit notification seat.
/// Nullable locations preserve the request store's own unknown sentinel.
/// </summary>
public sealed record OfferReceivedNotificationContext(
    string? PickupAddress,
    string? DropoffAddress,
    int EtaMinutes,
    DateTimeOffset CreatedAt);

/// <summary>
/// Concrete notification-service wire DTO for <c>jeeb.offer_received</c>.
/// The closed payload deliberately contains no object-typed escape hatch.
/// </summary>
public sealed record OfferReceivedNotificationRecord
{
    public const string TemplateKey = "jeeb.offer_received";

    public required string Sender { get; init; }
    public required string Receiver { get; init; }

    [JsonPropertyName("notification_id")]
    public required string NotificationCorrelationId { get; init; }

    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public required string Description { get; init; }

    [JsonPropertyName("notification_type")]
    public string NotificationType { get; init; } = TemplateKey;

    public required OfferReceivedNotificationPayload Payload { get; init; }

    [JsonPropertyName("senderProfilePicture")]
    public string SenderProfilePicture { get; init; } = string.Empty;

    public string Nickname { get; init; } = string.Empty;
}

/// <summary>The nine-field closed payload published by notification-service.</summary>
public sealed record OfferReceivedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("offer_id")]
    public required string OfferId { get; init; }

    /// <summary>The owning request: every offer destination is keyed by request id,
    /// so a notification without this cannot deep-link (G9/T3b).</summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_name")]
    public string ClientName { get; init; } = string.Empty;

    [JsonPropertyName("pickup_location")]
    public string PickupLocation { get; init; } = string.Empty;

    [JsonPropertyName("delivery_location")]
    public string DeliveryLocation { get; init; } = string.Empty;

    [JsonPropertyName("offer_amount")]
    public required decimal OfferAmount { get; init; }

    [JsonPropertyName("delivery_fee")]
    public required decimal DeliveryFee { get; init; }

    [JsonPropertyName("estimated_duration")]
    public required string EstimatedDuration { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// Concrete notification-service wire DTO for <c>jeeb.offer_accepted</c>.
/// </summary>
public sealed record OfferAcceptedNotificationRecord
{
    public const string TemplateKey = "jeeb.offer_accepted";

    public required string Sender { get; init; }
    public required string Receiver { get; init; }

    [JsonPropertyName("notification_id")]
    public required string NotificationCorrelationId { get; init; }

    public required string Title { get; init; }
    public string Subtitle { get; init; } = string.Empty;
    public required string Description { get; init; }

    [JsonPropertyName("notification_type")]
    public string NotificationType { get; init; } = TemplateKey;

    public required OfferAcceptedNotificationPayload Payload { get; init; }

    [JsonPropertyName("senderProfilePicture")]
    public string SenderProfilePicture { get; init; } = string.Empty;

    public string Nickname { get; init; } = string.Empty;
}

/// <summary>The eight-field closed payload published by notification-service.</summary>
public sealed record OfferAcceptedNotificationPayload
{
    [JsonPropertyName("user_id")]
    public required string UserId { get; init; }

    [JsonPropertyName("offer_id")]
    public required string OfferId { get; init; }

    /// <summary>The owning request: every offer destination is keyed by request id,
    /// so a notification without this cannot deep-link (G9/T3b).</summary>
    [JsonPropertyName("request_id")]
    public required string RequestId { get; init; }

    [JsonPropertyName("client_name")]
    public string ClientName { get; init; } = string.Empty;

    [JsonPropertyName("pickup_location")]
    public string PickupLocation { get; init; } = string.Empty;

    [JsonPropertyName("delivery_location")]
    public string DeliveryLocation { get; init; } = string.Empty;

    [JsonPropertyName("accepted_amount")]
    public required decimal AcceptedAmount { get; init; }

    [JsonPropertyName("jeeber_id")]
    public required string JeeberId { get; init; }

    [JsonPropertyName("created_at")]
    public required DateTimeOffset CreatedAt { get; init; }
}
