using JeebGateway.Notifications;

namespace JeebGateway.IntegrationTests;

// Replaces the per-file IPushNotificationService doubles the deleted in-gateway stack needed.
public sealed class CapturingGenericEventDispatcher : IGenericEventDispatcher
{
    public sealed record Handover(
        string EventType, string Receiver, string EntityId, string Title, string Body,
        IReadOnlyDictionary<string, string> Data, string Category);

    private readonly GenericEventDispatchClassification _classification;

    public CapturingGenericEventDispatcher(
        GenericEventDispatchClassification classification = GenericEventDispatchClassification.Accepted)
        => _classification = classification;

    public List<Handover> Sent { get; } = new();

    public Task<GenericEventDispatchOutcome> DispatchAsync(
        string eventType, string receiver, string entityId, string title, string body,
        IReadOnlyDictionary<string, string> data, string refreshCategory, CancellationToken ct)
    {
        Sent.Add(new Handover(eventType, receiver, entityId, title, body, data, refreshCategory));
        return Task.FromResult(new GenericEventDispatchOutcome(_classification, 200));
    }
}
