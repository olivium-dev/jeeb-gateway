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

    private async Task<NotificationRecordWriteOutcome> WriteAsync(
        string templateKey,
        string recipientId,
        string entityId,
        string notificationCorrelationId,
        Func<CancellationToken, Task<HttpStatusCode>> post)
    {
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
            description: "Durable notification rows skipped because authoritative required data was absent.");
}
