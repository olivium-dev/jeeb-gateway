namespace JeebGateway.Notifications;

public enum NotificationRecordWriteClassification
{
    Disabled,
    Committed,
    CommittedAfterAmbiguousResponse,
    Unproven,

    /// <summary>
    /// b02 step 3 — <see cref="PushSilencePolicy"/> classified this notification type as a
    /// SILENT refresh signal, so no notification-centre row was written and no POST was
    /// issued. This is the CORRECT terminal outcome, not a failure: a silent push is
    /// edge-triggered and a centre row is level-held state (see PushSilencePolicy for the
    /// full rationale). Never "repair" this by retrying, back-filling, or writing a hidden
    /// / soft-deleted / pre-read row.
    /// </summary>
    SkippedSilent,
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
