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

    /// <summary>
    /// The upstream answered 404/405: the typed notification-centre route for this template key
    /// does NOT EXIST in the deployed notification-service. Its type routes are built from the
    /// deployment profile (<c>NOTIFICATION_CONFIG_PATH</c>), and a deployment that ships the
    /// image default declares none of the <c>jeeb.*</c> types — so the POST cannot have stored a
    /// row and no upstream push will follow.
    ///
    /// <para>This is deliberately NOT <see cref="Unproven"/>. Unproven means "the POST may have
    /// committed", which every seat correctly reads as "upstream owns the push". A route that
    /// does not exist cannot own anything, and folding the two together is how
    /// <c>jeeb.offer_received</c> reached ZERO producers on staging while logging only a single
    /// Error line (G6, 2026-09-04). Callers must treat this as "no producer" and hand the event
    /// to the generic <c>POST /notifications/events</c> seam, which is a static route.</para>
    /// </summary>
    RouteAbsent,
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

    // ── b02 step 6a — the six previously-unwritten notification-centre types ─────────
    //
    // Every one goes through the same NotificationRecordWriter.WriteAsync choke point as the
    // two above, so all of them inherit step 3's silent gate, the single-attempt-plus-read-back
    // classification, and the per-attempt budget. That uniformity is the point: a writer that
    // reached the centre by any other route would be outside the silent policy.

    /// <summary>
    /// <b>Writes a row.</b> The "returns <see cref="NotificationRecordWriteClassification.SkippedSilent"/>
    /// ALWAYS, today" claim that used to sit here described owner ruling D4 (2026-07-26) and was
    /// <b>reversed on 2026-07-27</b>: the <c>delivery</c> category is shade + stored, so this
    /// writer is NOT silent-gated. See <see cref="DeliveryStatusUpdatedNotificationRecord"/>.
    /// </summary>
    Task<NotificationRecordWriteOutcome> WriteDeliveryStatusUpdatedAsync(
        DeliveryStatusUpdatedNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteSettlementPaidAsync(
        SettlementPaidNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteKycApprovedAsync(
        KycApprovedNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteKycRejectedAsync(
        KycRejectedNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteDisputeResolvedAsync(
        DisputeResolvedNotificationRecord record,
        CancellationToken requestToken);

    Task<NotificationRecordWriteOutcome> WriteRatingAutoRevealedAsync(
        RatingAutoRevealedNotificationRecord record,
        CancellationToken requestToken);
}
