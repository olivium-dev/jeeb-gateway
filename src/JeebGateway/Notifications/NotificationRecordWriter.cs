using System.Diagnostics.Metrics;
using System.Net;
using JeebGateway.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

/// <summary>
/// Single-attempt notification-service writer. A non-2xx or transport exception
/// is ambiguous because the upstream can commit before returning HTTP 500; one
/// exact-correlation read-back classifies that outcome without ever retrying POST.
///
/// <para>This type is the SOLE caller of <see cref="JeebNotificationRecordClient"/>, which
/// makes it the one choke point where the silent-vs-stored decision can be enforced for
/// every present and future centre write. b02 step 3 puts that gate here — see
/// <see cref="PushSilencePolicy"/> for why a silent push must produce NO row at all.</para>
/// </summary>
public sealed class NotificationRecordWriter : INotificationRecordWriter
{
    public const string EnabledConfigurationKey =
        "FeatureFlags:NotificationDurableWrite:Enabled";

    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(2);

    private readonly JeebNotificationRecordClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<NotificationRecordWriter> _logger;

    public NotificationRecordWriter(
        JeebNotificationRecordClient client,
        IConfiguration configuration,
        ILogger<NotificationRecordWriter> logger)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    public Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
        OfferReceivedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            OfferReceivedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.OfferId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostOfferReceivedAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
        OfferAcceptedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            OfferAcceptedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.OfferId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostOfferAcceptedAsync(record, cancellationToken));

    // ── b02 step 6a — the six previously-unwritten types ─────────────────────────────
    //
    // Each is the same three lines: template key, recipient, the entity id the log/read-back
    // keys on, and the one POST. Nothing type-specific happens in this class — the silent gate,
    // the budget and the ambiguity classification below apply identically to all eight. The
    // `entityId` chosen per type is the id an operator would grep for when a row is missing.

    public Task<NotificationRecordWriteOutcome> WriteDeliveryStatusUpdatedAsync(
        DeliveryStatusUpdatedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            DeliveryStatusUpdatedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.DeliveryId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostDeliveryStatusUpdatedAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteSettlementPaidAsync(
        SettlementPaidNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            SettlementPaidNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.SettlementId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostSettlementPaidAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteKycApprovedAsync(
        KycApprovedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            KycApprovedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.KycId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostKycApprovedAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteKycRejectedAsync(
        KycRejectedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            KycRejectedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.KycId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostKycRejectedAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteDisputeResolvedAsync(
        DisputeResolvedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            DisputeResolvedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.DisputeId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostDisputeResolvedAsync(record, cancellationToken));

    public Task<NotificationRecordWriteOutcome> WriteRatingAutoRevealedAsync(
        RatingAutoRevealedNotificationRecord record,
        CancellationToken requestToken)
        => WriteAsync(
            RatingAutoRevealedNotificationRecord.TemplateKey,
            record.Receiver,
            record.Payload.DeliveryId,
            record.NotificationCorrelationId,
            cancellationToken => _client.PostRatingAutoRevealedAsync(record, cancellationToken));

    /// <summary>
    /// The one path to the notification centre. <c>internal</c> rather than <c>private</c>
    /// so the silent gate below can be exercised directly against a notification type that
    /// has no public writer yet — the gate must be tested at the choke point real callers
    /// use, not at a stand-in.
    /// </summary>
    internal async Task<NotificationRecordWriteOutcome> WriteAsync(
        string templateKey,
        string recipientId,
        string entityId,
        string notificationCorrelationId,
        Func<CancellationToken, Task<HttpStatusCode>> post)
    {
        // ── SILENT ⇒ NO ROW. Checked FIRST, ahead of the feature flag, because this is a
        // policy invariant and not a feature: there is no configuration under which a
        // silent refresh signal may be stored.
        //
        // DO NOT "fix" this into a soft-deleted row, a hidden flag, or is_read=true. A
        // silent push is EDGE-triggered (its only meaning is "the data changed just now,
        // fetch once"); a centre row is LEVEL-held state that survives restart, is scrolled
        // days later, and is replayable. Storing an edge as level is the same category error
        // as a publisher with no subscriber: correct-looking in review, junk at runtime. And
        // a DLQ replay of a stale refresh signal causes a spurious fetch, so the row is not
        // merely useless — it is actively harmful.
        //
        // The corollary, so it is not lost: a change that is BOTH worth telling the user
        // about AND requires a UI refresh is ONE non-silent stored notification whose data
        // block also carries the refresh category — NOT two pushes. Two is how you get a
        // duplicated shade. PushSilencePolicy makes that structural: one type, one mode.
        if (PushSilencePolicy.IsSilent(templateKey))
        {
            // entityId is deliberately NOT a metric tag here, even though the sibling
            // skip in OfferPushNotifier.cs tags it. This meter
            // (BusinessOutcomeTelemetry.MeterName) is Prometheus-exported (Program.cs),
            // so every tag becomes a label and every distinct value becomes its own time
            // series. That is tolerable for the sibling, which fires only on a rare data
            // defect; this counter fires on EVERY silent refresh — the whole volume the
            // polling→push migration is moving onto this path — so tagging it per entity
            // is an unbounded-cardinality bomb. The id stays in the structured log below,
            // where per-event cardinality is free and the debugging value actually is.
            //
            // `durable_write_enabled` is here because this gate sits AHEAD of the feature
            // flag. Without it, an operator seeing a non-zero notif.durable_write.skipped
            // would reasonably conclude the durable-write path is live, when it may be
            // switched off entirely — the two states are behaviourally identical (nothing
            // is written either way) but not diagnostically identical. Two values only, so
            // unlike entityId this tag is cardinality-safe.
            var durableWriteEnabled = _configuration.GetValue<bool>(EnabledConfigurationKey);
            NotificationDurableWriteTelemetry.Skipped.Add(
                1,
                new("type", templateKey),
                new("reason", "silent_is_not_inbox_state"),
                new("durable_write_enabled", durableWriteEnabled ? "true" : "false"));
            _logger.LogDebug(
                "event={event} type={type} reason={reason} recipientId={recipientId} " +
                "entityId={entityId} ncid={ncid} durableWriteEnabled={durableWriteEnabled}",
                "notif.durable_write.skipped",
                templateKey,
                "silent_is_not_inbox_state",
                recipientId,
                entityId,
                notificationCorrelationId,
                durableWriteEnabled);
            return new(NotificationRecordWriteClassification.SkippedSilent, null);
        }

        if (!_configuration.GetValue<bool>(EnabledConfigurationKey))
        {
            return new(NotificationRecordWriteClassification.Disabled, null);
        }

        int? upstreamStatus = null;
        try
        {
            using var postBudget = new CancellationTokenSource(AttemptBudget);
            var status = await post(postBudget.Token).ConfigureAwait(false);
            upstreamStatus = (int)status;
            if (status is HttpStatusCode.OK or HttpStatusCode.Created)
            {
                return new(NotificationRecordWriteClassification.Committed, upstreamStatus);
            }
        }
        catch (Exception)
        {
            // Transport faults and post-send cancellation are ambiguous. Do not
            // pattern-match an upstream error body and never issue another POST.
        }

        var found = false;
        try
        {
            // The read-back receives its own independent budget so a POST timeout
            // cannot pre-cancel the only operation capable of classifying it.
            using var readBudget = new CancellationTokenSource(AttemptBudget);
            found = await _client
                .ContainsCorrelationIdAsync(
                    recipientId,
                    notificationCorrelationId,
                    readBudget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            found = false;
        }

        if (found)
        {
            NotificationDurableWriteTelemetry.Outcomes.Add(
                1,
                new("classification", "committed_after_ambiguous_response"),
                new("templateKey", templateKey));
            _logger.LogInformation(
                "event={event} classification={classification} templateKey={templateKey} " +
                "recipientId={recipientId} entityId={entityId} ncid={ncid} upstreamStatus={upstreamStatus}",
                "notif.durable_write.classified",
                "committed_after_ambiguous_response",
                templateKey,
                recipientId,
                entityId,
                notificationCorrelationId,
                upstreamStatus);
            return new(
                NotificationRecordWriteClassification.CommittedAfterAmbiguousResponse,
                upstreamStatus);
        }

        NotificationDurableWriteTelemetry.Outcomes.Add(
            1,
            new("classification", "unproven"),
            new("templateKey", templateKey));
        _logger.LogError(
            "event={event} classification={classification} templateKey={templateKey} " +
            "recipientId={recipientId} entityId={entityId} ncid={ncid} upstreamStatus={upstreamStatus}",
            "notif.durable_write.failed",
            "unproven",
            templateKey,
            recipientId,
            entityId,
            notificationCorrelationId,
            upstreamStatus);
        return new(NotificationRecordWriteClassification.Unproven, upstreamStatus);
    }
}

internal static class NotificationDurableWriteTelemetry
{
    private static readonly Meter Meter = new(BusinessOutcomeTelemetry.MeterName);

    internal static readonly Counter<long> Outcomes =
        Meter.CreateCounter<long>(
            "notif.durable_write.outcomes",
            description: "Durable notification write outcomes requiring operator attention.");

    internal static readonly Counter<long> FieldAbsent =
        Meter.CreateCounter<long>(
            "notif.durable_write.field_absent",
            description: "Required notification fields populated with an owning-store absence sentinel.");

    internal static readonly Counter<long> Skipped =
        Meter.CreateCounter<long>(
            "notif.durable_write.skipped",
            description:
                "Durable notification rows not written. Disambiguate on the `reason` tag: "
                + "`silent_is_not_inbox_state` is the CORRECT b02 step-3 outcome for a silent "
                + "refresh signal and must not be alerted on; the data-absence reasons are "
                + "genuine defects.");
}
