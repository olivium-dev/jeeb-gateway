using System.Diagnostics.Metrics;
using JeebGateway.Observability;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Notifications;

// The one hand-over seam for push consumers, and the one place a failed hand-over is loud.
// Replaces the deleted in-gateway IPushNotificationService, whose sends always resolved NoDevices.
public static class PushHandover
{
    // Structured log event for "this notification reached no producer".
    public const string NoProducerEvent = "notif.push.no_producer";

    private static readonly Meter Meter = new(BusinessOutcomeTelemetry.MeterName);

    // Every increment is one notification nobody will deliver. Alert on this.
    public static readonly Counter<long> Unproduced =
        Meter.CreateCounter<long>(
            "notif.push_handover.unproduced",
            description: "Pushes handed over to no durable producer. Each one is a lost notification.");

    // Deduplicated counts as owned: another producer already holds the same notification_id.
    public static bool IsProducerOwned(GenericEventDispatchClassification classification) =>
        classification is GenericEventDispatchClassification.Accepted
            or GenericEventDispatchClassification.Deduplicated
            or GenericEventDispatchClassification.AcceptedAfterAmbiguousResponse;

    /// <summary>
    /// Hand one event to notification-service. Never throws except on caller cancellation;
    /// a non-producing classification or a thrown dispatcher is counted AND logged at Error.
    /// </summary>
    /// <param name="entityId">
    /// The consumer's OWN idempotency key — hashed with (eventType, receiver) into the
    /// notification_id. Re-running one logical event must pass the same string.
    /// </param>
    public static async Task<GenericEventDispatchClassification> DispatchAsync(
        IGenericEventDispatcher events,
        ILogger logger,
        string eventType,
        string receiver,
        string entityId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        string refreshCategory,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(logger);

        GenericEventDispatchClassification classification;
        try
        {
            var outcome = await events
                .DispatchAsync(eventType, receiver, entityId, title, body, data, refreshCategory, ct)
                .ConfigureAwait(false);
            classification = outcome.Classification;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Alarm(logger, ex, eventType, receiver, entityId, "threw");
            return GenericEventDispatchClassification.Unproven;
        }

        if (!IsProducerOwned(classification))
        {
            Alarm(logger, null, eventType, receiver, entityId, classification.ToString());
        }

        return classification;
    }

    private static void Alarm(
        ILogger logger,
        Exception? ex,
        string eventType,
        string receiver,
        string entityId,
        string classification)
    {
        Unproduced.Add(
            1,
            new KeyValuePair<string, object?>("eventType", eventType),
            new KeyValuePair<string, object?>("classification", classification));

        logger.Log(
            LogLevel.Error,
            default,
            ex,
            "event={event} eventType={eventType} recipientId={recipientId} entityId={entityId} "
            + "classification={classification}",
            NoProducerEvent, eventType, receiver, entityId, classification);
    }
}
