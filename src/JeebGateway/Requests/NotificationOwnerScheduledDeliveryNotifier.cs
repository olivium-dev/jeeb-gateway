using JeebGateway.Notifications;

namespace JeebGateway.Requests;

/// <summary>Scheduled matching reminders published to notification-service.</summary>
public sealed class NotificationOwnerScheduledDeliveryNotifier(
    INotificationOwnerClient owner) : IScheduledDeliveryNotifier
{
    public Task NotifyClientMatchingWindowOpenedAsync(
        string clientId,
        string requestId,
        DateTimeOffset scheduledAt,
        DateTimeOffset at,
        CancellationToken ct)
    {
        var title = "Scheduled delivery";
        var body = "Your scheduled delivery is matching now.";
        return owner.PublishAsync(new NotificationOwnerEvent(
            NotificationOwnerEventId.FromIdempotencyKey("scheduled-matching:" + requestId),
            clientId,
            title,
            body,
            "gateway.scheduled_matching_opened",
            new Dictionary<string, object?>
            {
                ["request_id"] = requestId,
                ["scheduled_at"] = scheduledAt.ToUniversalTime(),
                ["opened_at"] = at.ToUniversalTime(),
            }), ct);
    }
}
