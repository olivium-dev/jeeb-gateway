namespace JeebGateway.Notifications;

public enum GenericEventDispatchClassification
{
    /// <summary>The gateway is still the direct push producer; nothing was handed over.</summary>
    SkippedDirectDispatchArmed,

    Accepted,

    /// <summary>Another producer already owns this correlation id — the whole point of the key.</summary>
    Deduplicated,

    /// <summary>Ambiguous POST, and the read-back proved the command is stored.</summary>
    AcceptedAfterAmbiguousResponse,

    Unproven,
}

public sealed record GenericEventDispatchOutcome(
    GenericEventDispatchClassification Classification,
    int? UpstreamStatus);

/// <summary>
/// Hands a push kind that has NO notification-centre route to the shared service's generic
/// durable-event seam, so it survives the gateway ceasing to be a push producer.
/// </summary>
public interface IGenericEventDispatcher
{
    /// <summary>
    /// Never throws. <paramref name="entityId"/> plus <paramref name="eventType"/> and
    /// <paramref name="receiver"/> mint the shared idempotency key, so every producer of one
    /// logical event must pass the same three.
    /// </summary>
    Task<GenericEventDispatchOutcome> DispatchAsync(
        string eventType,
        string receiver,
        string entityId,
        string title,
        string body,
        IReadOnlyDictionary<string, string> data,
        string refreshCategory,
        CancellationToken ct);
}
