namespace JeebGateway.Push;

public enum PushDeliveryOutcome
{
    /// <summary>Successfully handed off to at least one transport on first attempt.</summary>
    Delivered,

    /// <summary>First attempt failed, scheduled for the single 30-second retry.</summary>
    QueuedForRetry,

    /// <summary>
    /// Delivered on the retry attempt after the first attempt failed.
    /// Recorded separately so observability/SLO dashboards can see how
    /// often the retry path saves a delivery.
    /// </summary>
    DeliveredOnRetry,

    /// <summary>Both attempts failed — this is a hard delivery failure.</summary>
    Failed,

    /// <summary>User has the category muted — never sent, not retried.</summary>
    SuppressedByPreference,

    /// <summary>User has no registered devices — nothing to send.</summary>
    NoDevices
}

public sealed record PushDeliveryResult(
    string UserId,
    NotificationTrigger Trigger,
    PushDeliveryOutcome Outcome,
    int AttemptsMade,
    string? Reason = null);
