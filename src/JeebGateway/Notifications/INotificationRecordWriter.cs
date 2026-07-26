namespace JeebGateway.Notifications;

public enum NotificationRecordWriteClassification
{
    Disabled,
    Committed,
    CommittedAfterAmbiguousResponse,
    Unproven,
}

public sealed record NotificationRecordWriteOutcome(
    NotificationRecordWriteClassification Classification,
    int? UpstreamStatus);

/// <summary>
/// Best-effort post-commit durable record writer. Implementations never throw
/// and never issue a second POST for one emission.
/// </summary>
public interface INotificationRecordWriter
{
    Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
        OfferReceivedNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
        OfferAcceptedNotificationRecord record,
        CancellationToken requestToken);
}
