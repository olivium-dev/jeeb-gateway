using JeebGateway.Notifications;

namespace JeebGateway.Push;

/// <summary>
/// Compatibility adapter for legacy gateway call sites. A successful return
/// means notification-service durably accepted the command; delivery and all
/// retry/tracker state remain exclusively owner-managed.
/// </summary>
public sealed class NotificationOwnerPushService : IPushNotificationService
{
    private readonly INotificationOwnerClient _owner;

    public NotificationOwnerPushService(INotificationOwnerClient owner)
    {
        _owner = owner;
    }

    public async Task<PushDeliveryResult> SendAsync(
        PushNotificationRequest request,
        CancellationToken ct)
    {
        var notificationId = NotificationOwnerEventId.FromIdempotencyKey(request.IdempotencyKey);
        var data = request.Data?.ToDictionary(
                       pair => pair.Key,
                       pair => (object?)pair.Value,
                       StringComparer.Ordinal)
                   ?? new Dictionary<string, object?>();

        await _owner.PublishAsync(
            new NotificationOwnerEvent(
                notificationId,
                request.UserId,
                request.Title,
                request.Body,
                $"gateway.{request.Trigger.ToString().ToLowerInvariant()}",
                data,
                string.IsNullOrWhiteSpace(request.Language) ? "en" : request.Language),
            ct);

        return new PushDeliveryResult(
            request.UserId,
            request.Trigger,
            PushDeliveryOutcome.QueuedForRetry,
            AttemptsMade: 0,
            Reason: $"durably_accepted_by_notification_owner:{notificationId:D}");
    }
}
