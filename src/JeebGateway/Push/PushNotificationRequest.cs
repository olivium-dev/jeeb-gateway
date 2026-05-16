namespace JeebGateway.Push;

/// <summary>
/// One in-product event that should fan out to a user's registered devices.
/// Callers (sweepers, controllers, downstream services) hand this to
/// <see cref="IPushNotificationService.SendAsync"/>; the service handles
/// preference filtering, token resolution, transport selection and retry.
/// </summary>
public sealed record PushNotificationRequest(
    string UserId,
    NotificationTrigger Trigger,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string>? Data = null,
    string? IdempotencyKey = null);
