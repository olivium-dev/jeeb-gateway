using System.Net;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Options;

namespace JeebGateway.Notifications;

/// <summary>
/// Single-attempt generic-event dispatcher. Runs only while gateway direct push dispatch is
/// DISABLED, so the gateway is never both the direct producer and the hand-over producer.
/// </summary>
public sealed class GenericEventDispatcher : IGenericEventDispatcher
{
    private static readonly TimeSpan AttemptBudget = TimeSpan.FromSeconds(2);

    private readonly JeebNotificationRecordClient _client;
    private readonly IOptions<GatewayDirectPushDispatchOptions> _directDispatch;
    private readonly ILogger<GenericEventDispatcher> _logger;

    public GenericEventDispatcher(
        JeebNotificationRecordClient client,
        IOptions<GatewayDirectPushDispatchOptions> directDispatch,
        ILogger<GenericEventDispatcher> logger)
    {
        _client = client;
        _directDispatch = directDispatch;
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

        if (_directDispatch.Value.Enabled)
        {
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
        catch (Exception)
        {
            // The upstream can commit and then fail the response, so only a read-back can classify.
        }

        var found = false;
        try
        {
            using var readBudget = new CancellationTokenSource(AttemptBudget);
            found = await _client
                .ContainsCorrelationIdAsync(receiver, correlationId, readBudget.Token)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
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
