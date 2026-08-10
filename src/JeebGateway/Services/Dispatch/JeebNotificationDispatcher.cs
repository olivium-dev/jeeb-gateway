using JeebGateway.Push;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Concrete implementation of <see cref="IJeebNotificationDispatcher"/>.
///
/// Rendering remains a compatibility concern for the deprecated gateway route;
/// notification-service owns persistence, dispatch, retries, DLQ, device-token
/// resolution, and delivery tracking.
/// </summary>
public sealed class JeebNotificationDispatcher : IJeebNotificationDispatcher
{
    private readonly INotificationTemplateRenderer _renderer;
    private readonly IPushNotificationService _push;
    private readonly ILogger<JeebNotificationDispatcher> _logger;

    public JeebNotificationDispatcher(
        INotificationTemplateRenderer renderer,
        IPushNotificationService push,
        ILogger<JeebNotificationDispatcher> logger)
    {
        _renderer = renderer;
        _push = push;
        _logger = logger;
    }

    public async Task<NotificationDispatchResult> DispatchAsync(
        string templateKey,
        string locale,
        Dictionary<string, string> parameters,
        Guid recipientUserId,
        string? idempotencyKey = null,
        CancellationToken ct = default)
    {
        var entryId = JeebGateway.Notifications.NotificationOwnerEventId
            .FromIdempotencyKey(idempotencyKey);
        var rendered = _renderer.Render(templateKey, locale, parameters);
        if (rendered is null)
        {
            var error = $"Unknown template key '{templateKey}'.";
            _logger.LogWarning("Notification dispatch rejected: {Error} EntryId={EntryId}", error, entryId);
            return new NotificationDispatchResult(entryId, WasDeduplicated: false, NotificationDispatchStatus.DLQ, error);
        }

        var pushRequest = new PushNotificationRequest(
            UserId: recipientUserId.ToString(),
            Trigger: NotificationTrigger.StatusChange,
            Title: rendered.Title,
            Body: rendered.Body,
            Data: parameters,
            IdempotencyKey: entryId.ToString("D"),
            Language: locale);

        var result = await _push.SendAsync(pushRequest, ct);

        _logger.LogInformation(
            "Notification command accepted by owner. EntryId={EntryId} Outcome={Outcome}",
            entryId, result.Outcome);
        return new NotificationDispatchResult(
            entryId,
            WasDeduplicated: false,
            NotificationDispatchStatus.Pending,
            result.Reason);
    }
}
