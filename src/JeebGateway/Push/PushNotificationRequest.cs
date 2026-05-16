namespace JeebGateway.Push;

/// <summary>
/// One in-product event that should fan out to a user's registered devices.
/// Callers (sweepers, controllers, downstream services) hand this to
/// <see cref="IPushNotificationService.SendAsync"/>; the service handles
/// preference filtering, token resolution, transport selection and retry.
///
/// <para><c>Language</c> is the BCP-47 tag the notification body was rendered in.
/// Callers can pass it through explicitly when they already have a localised
/// payload; otherwise the unified service resolves it from the user's persisted
/// profile (T-backend-029 AC #6) so transports — and the FCM notification
/// channel routing — can carry the correct locale.</para>
/// </summary>
public sealed record PushNotificationRequest(
    string UserId,
    NotificationTrigger Trigger,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Data = null,
    string? IdempotencyKey = null,
    string? Language = null);
