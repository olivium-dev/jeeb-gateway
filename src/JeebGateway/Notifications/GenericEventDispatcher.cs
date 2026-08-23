using System.Net;
using Microsoft.Extensions.Configuration;

namespace JeebGateway.Notifications;

/// <summary>
/// Single-attempt generic-event dispatcher. notification-service is the settled
/// push producer, so every generic event is handed over through this path.
/// </summary>
public sealed class GenericEventDispatcher : IGenericEventDispatcher
{
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(2);

    private readonly JeebNotificationRecordClient _client;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GenericEventDispatcher> _logger;

    public GenericEventDispatcher(
        JeebNotificationRecordClient client,
        IConfiguration configuration,
        ILogger<GenericEventDispatcher> logger)
    {
        _client = client;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GenericEventDispatchOutcome> DispatchAsync(
        string eventType,
        string receiver,
        string entityId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        string refreshCategory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(eventType)
            || string.IsNullOrWhiteSpace(receiver)
            || string.IsNullOrWhiteSpace(entityId))
        {
            return new(GenericEventDispatchClassification.Unproven, null);
        }

        // Same gate as NotificationRecordWriter: the durable-write flag is what says the
        // gateway may talk to the notification centre at all.
        if (!_configuration.GetValue<bool>(NotificationRecordWriter.EnabledConfigurationKey))
        {
            _logger.LogError(
                "event={event} eventType={eventType} recipientId={recipientId} entityId={entityId} "
                + "detail={detail}",
                "notif.generic_event.no_producer", eventType, receiver, entityId,
                "direct push dispatch is disabled AND "
                + NotificationRecordWriter.EnabledConfigurationKey
                + " is false: this event has NO push producer");
            return new(GenericEventDispatchClassification.SkippedDirectDispatchArmed, null);
        }

        var correlationId = NotificationCorrelationId.Create(eventType, receiver, entityId);
        var record = BuildRecord(eventType, receiver, correlationId, title, body, data, refreshCategory);

        int? upstreamStatus = null;
        try
        {
            using var postBudget = new CancellationTokenSource(AttemptBudget);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, postBudget.Token);
            var status = await _client.PostGenericEventAsync(record, linked.Token).ConfigureAwait(false);
            upstreamStatus = (int)status;

            if (status is HttpStatusCode.OK or HttpStatusCode.Created or HttpStatusCode.Accepted)
            {
                return Classified(
                    GenericEventDispatchClassification.Accepted,
                    eventType, receiver, entityId, correlationId, upstreamStatus, LogLevel.Information);
            }

            if (status is HttpStatusCode.Conflict)
            {
                return Classified(
                    GenericEventDispatchClassification.Deduplicated,
                    eventType, receiver, entityId, correlationId, upstreamStatus, LogLevel.Information);
            }
        }
        catch (Exception ex)
        {
            // The upstream can commit and then fail the response, so only a read-back can classify.
            _logger.LogError(ex,
                "event={event} eventType={eventType} recipientId={recipientId} entityId={entityId} ncid={ncid}",
                "notif.generic_event.post_failed", eventType, receiver, entityId, correlationId);
        }

        var found = false;
        try
        {
            using var readBudget = new CancellationTokenSource(AttemptBudget);
            found = await _client
                .ContainsCorrelationIdAsync(receiver, correlationId, readBudget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "event={event} eventType={eventType} recipientId={recipientId} entityId={entityId} ncid={ncid}",
                "notif.generic_event.readback_failed", eventType, receiver, entityId, correlationId);
            found = false;
        }

        return Classified(
            found
                ? GenericEventDispatchClassification.AcceptedAfterAmbiguousResponse
                : GenericEventDispatchClassification.Unproven,
            eventType, receiver, entityId, correlationId, upstreamStatus,
            found ? LogLevel.Information : LogLevel.Error);
    }

    internal static GenericEventNotificationRecord BuildRecord(
        string eventType,
        string receiver,
        string correlationId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        string refreshCategory)
    {
        var routing = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in data)
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null)
            {
                routing[pair.Key] = pair.Value;
            }
        }

        routing["category"] = refreshCategory;
        routing["notificationId"] = correlationId;
        routing["notification_id"] = correlationId;

        // PushSilencePolicy is the ONE source of the silent decision, per its own class remarks.
        if (PushSilencePolicy.ModeForCategory(refreshCategory) == PushDeliveryMode.SilentRefresh)
        {
            routing["silent"] = "true";
        }

        return new GenericEventNotificationRecord
        {
            NotificationCorrelationId = correlationId,
            Receiver = receiver,
            Title = title,
            Body = body,
            EventType = eventType,
            Data = routing,
        };
    }

    private GenericEventDispatchOutcome Classified(
        GenericEventDispatchClassification classification,
        string eventType,
        string receiver,
        string entityId,
        string correlationId,
        int? upstreamStatus,
        LogLevel level)
    {
        NotificationDurableWriteTelemetry.Outcomes.Add(
            1,
            new("classification", classification.ToString()),
            new("templateKey", eventType));
        _logger.Log(
            level,
            "event={event} classification={classification} eventType={eventType} "
            + "recipientId={recipientId} entityId={entityId} ncid={ncid} upstreamStatus={upstreamStatus}",
            "notif.generic_event.dispatched",
            classification,
            eventType,
            receiver,
            entityId,
            correlationId,
            upstreamStatus);
        return new(classification, upstreamStatus);
    }
}

/// <summary>Flag-off / test double: hands nothing over and never throws.</summary>
public sealed class NullGenericEventDispatcher : IGenericEventDispatcher
{
    public static readonly NullGenericEventDispatcher Instance = new();

    public Task<GenericEventDispatchOutcome> DispatchAsync(
        string eventType,
        string receiver,
        string entityId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        string refreshCategory,
        CancellationToken ct)
        => Task.FromResult(new GenericEventDispatchOutcome(
            GenericEventDispatchClassification.SkippedDirectDispatchArmed, null));
}
