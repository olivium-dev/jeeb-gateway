using JeebGateway.Push;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Concrete implementation of <see cref="IJeebNotificationDispatcher"/>.
///
/// <para><b>Durability model (MVP).</b>  The outbox is backed by an
/// <see cref="InMemoryNotificationDispatchOutbox"/>; swap the DI binding for a
/// Postgres-backed store to get crash-safe persistence. The rest of this class
/// is storage-agnostic.</para>
///
/// <para><b>Push transport.</b>  Uses the existing
/// <see cref="IPushNotificationService"/> pipeline so preference filtering,
/// device-token resolution, and platform retry are all inherited for free.</para>
/// </summary>
public sealed class JeebNotificationDispatcher : IJeebNotificationDispatcher
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly INotificationTemplateRenderer _renderer;
    private readonly IPushNotificationService _push;
    private readonly INotificationDispatchOutbox _outbox;
    private readonly ILogger<JeebNotificationDispatcher> _logger;

    public JeebNotificationDispatcher(
        INotificationTemplateRenderer renderer,
        IPushNotificationService push,
        INotificationDispatchOutbox outbox,
        ILogger<JeebNotificationDispatcher> logger)
    {
        _renderer = renderer;
        _push = push;
        _outbox = outbox;
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
        // 1. Idempotency check
        if (idempotencyKey is not null)
        {
            var exists = await _outbox.ExistsAsync(idempotencyKey, ct);
            if (exists)
            {
                _logger.LogInformation(
                    "Notification dispatch deduplicated. IdempotencyKey={Key} TemplateKey={Template} Recipient={UserId}",
                    idempotencyKey, templateKey, recipientUserId);
                return new NotificationDispatchResult(Guid.Empty, WasDeduplicated: true, NotificationDispatchStatus.Delivered);
            }
        }

        // 2. Persist to outbox
        var entry = new NotificationDispatchEntry
        {
            TemplateKey = templateKey,
            Locale = locale,
            Parameters = parameters,
            RecipientUserId = recipientUserId,
            IdempotencyKey = idempotencyKey,
        };
        await _outbox.AddAsync(entry, ct);

        _logger.LogInformation(
            "Notification dispatch enqueued. EntryId={EntryId} TemplateKey={Template} Locale={Locale} Recipient={UserId}",
            entry.Id, templateKey, locale, recipientUserId);

        // 3. Render template
        var rendered = _renderer.Render(templateKey, locale, parameters);
        if (rendered is null)
        {
            var error = $"Unknown template key '{templateKey}'.";
            _logger.LogWarning("Notification dispatch failed: {Error} EntryId={EntryId}", error, entry.Id);
            await _outbox.RecordFailureAsync(entry.Id, error, MaxAttempts, RetryDelay, ct);
            return new NotificationDispatchResult(entry.Id, WasDeduplicated: false, NotificationDispatchStatus.DLQ, error);
        }

        // 4. Push dispatch
        try
        {
            var pushRequest = new PushNotificationRequest(
                UserId: recipientUserId.ToString(),
                Trigger: NotificationTrigger.StatusChange,
                Title: rendered.Title,
                Body: rendered.Body,
                Data: parameters,
                IdempotencyKey: idempotencyKey,
                Language: locale);

            var result = await _push.SendAsync(pushRequest, ct);

            _logger.LogInformation(
                "Notification push dispatched. EntryId={EntryId} Outcome={Outcome}",
                entry.Id, result.Outcome);

            await _outbox.MarkDeliveredAsync(entry.Id, ct);
            return new NotificationDispatchResult(entry.Id, WasDeduplicated: false, NotificationDispatchStatus.Delivered);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Notification push delivery failed. EntryId={EntryId} TemplateKey={Template} Attempt={Attempt}",
                entry.Id, templateKey, entry.AttemptCount + 1);

            await _outbox.RecordFailureAsync(entry.Id, ex.Message, MaxAttempts, RetryDelay, ct);
            return new NotificationDispatchResult(entry.Id, WasDeduplicated: false, NotificationDispatchStatus.Pending, ex.Message);
        }
    }
}
