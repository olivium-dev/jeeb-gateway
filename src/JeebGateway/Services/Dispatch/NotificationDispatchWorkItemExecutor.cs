#nullable enable

using JeebGateway.Migration;
using JeebGateway.Notifications;
using JeebGateway.StateService.Ownership;
using JeebGateway.StateService.Work;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Dispatch;

/// <summary>
/// W1-10 claim side of the notification outbox: renders and pushes an item the worker holds a
/// lease for. The inline dispatchers only enqueue once this owns dispatch.
/// </summary>
public sealed class NotificationDispatchWorkItemExecutor : IWorkItemExecutor
{
    private readonly INotificationTemplateRenderer _renderer;
    private readonly IGenericEventDispatcher _events;
    private readonly GwdbxMigrationPhase _phase;
    private readonly ILogger<NotificationDispatchWorkItemExecutor> _log;

    public NotificationDispatchWorkItemExecutor(
        INotificationTemplateRenderer renderer,
        IGenericEventDispatcher events,
        IOptions<GwdbxMigrationOptions> options,
        ILogger<NotificationDispatchWorkItemExecutor> log)
    {
        _renderer = renderer;
        _events = events;
        _phase = options.Value.NotificationOutbox;
        _log = log;
    }

    public string Kind => StateServiceNotificationDispatchOutbox.WorkItemKind;

    // Only the mode in which the state-service outbox is authoritative may claim its work;
    // NotificationOutboxMode defaults to "local", so this is false as shipped.
    public bool Enabled => _phase >= GwdbxMigrationPhase.UpstreamAuthority;

    public async Task ExecuteAsync(WorkItemRecordV1 item, CancellationToken ct)
    {
        var entry = StateServiceNotificationDispatchOutbox.ReadEntry(item);
        var rendered = _renderer.Render(entry.TemplateKey, entry.Locale, entry.Parameters)
            ?? throw new InvalidOperationException($"Unknown template key '{entry.TemplateKey}'.");

        var data = new Dictionary<string, string>(entry.Parameters, StringComparer.Ordinal)
        {
            ["type"] = entry.TemplateKey,
            ["language"] = entry.Locale,
        };

        // The entry's OWN idempotency key rides through as the entity id (§4.1 rider).
        var classification = await PushHandover.DispatchAsync(
            _events, _log, entry.TemplateKey, entry.RecipientUserId.ToString(),
            string.IsNullOrWhiteSpace(entry.IdempotencyKey) ? entry.Id.ToString() : entry.IdempotencyKey!,
            rendered.Title, rendered.Body, data,
            PushSilencePolicy.CategoryForTemplateKey(entry.TemplateKey) ?? PushSilencePolicy.CategoryDelivery,
            ct);

        // THROW, do not log-and-return: the lease holder must fail the work item so it retries.
        if (!PushHandover.IsProducerOwned(classification))
        {
            throw new InvalidOperationException(
                $"notification-service did not own the hand-over ({classification}).");
        }

        _log.LogInformation(
            "Claimed notification dispatched. WorkItemId={WorkItemId} EntryId={EntryId} Outcome={Outcome}",
            item.WorkItemId, entry.Id, classification);
    }
}
