#nullable enable

using JeebGateway.Migration;
using JeebGateway.Push;
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
    private readonly IPushNotificationService _push;
    private readonly GwdbxMigrationPhase _phase;
    private readonly ILogger<NotificationDispatchWorkItemExecutor> _log;

    public NotificationDispatchWorkItemExecutor(
        INotificationTemplateRenderer renderer,
        IPushNotificationService push,
        IOptions<GwdbxMigrationOptions> options,
        ILogger<NotificationDispatchWorkItemExecutor> log)
    {
        _renderer = renderer;
        _push = push;
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

        // The entry's OWN idempotency key rides through to PushDispatch (§4.1 rider).
        var result = await _push.SendAsync(
            new PushNotificationRequest(
                UserId: entry.RecipientUserId.ToString(),
                Trigger: NotificationTrigger.StatusChange,
                Title: rendered.Title,
                Body: rendered.Body,
                Data: entry.Parameters,
                IdempotencyKey: entry.IdempotencyKey,
                Language: entry.Locale),
            ct);

        _log.LogInformation(
            "Claimed notification dispatched. WorkItemId={WorkItemId} EntryId={EntryId} Outcome={Outcome}",
            item.WorkItemId, entry.Id, result.Outcome);
    }
}
