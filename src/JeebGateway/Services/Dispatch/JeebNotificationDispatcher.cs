using JeebGateway.Migration;
using JeebGateway.Push;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly ServicePushNotificationClient? _servicePush;
    private readonly GwdbxMigrationPhase _phase;

    public JeebNotificationDispatcher(
        INotificationTemplateRenderer renderer,
        IPushNotificationService push,
        INotificationDispatchOutbox outbox,
        ILogger<JeebNotificationDispatcher> logger,
        IOptions<GwdbxMigrationOptions>? migration = null,
        ServicePushNotificationClient? servicePush = null)
    {
        _renderer = renderer;
        _push = push;
        _outbox = outbox;
        _logger = logger;
        _servicePush = servicePush;
        _phase = migration?.Value.PushDispatch ?? GwdbxMigrationPhase.Local;
    }

    // W3-14: "local" keeps the in-gateway stack; only "upstream-authority" re-points, because a
    // dual-write rung would send each notification twice (the D1 duplicate-producer defect).
    private bool DispatchesUpstream =>
        _phase >= GwdbxMigrationPhase.UpstreamAuthority && _servicePush is not null;

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
            if (DispatchesUpstream)
            {
                await SendUpstreamAsync(
                    entry.Id, templateKey, locale, rendered, parameters, recipientUserId, idempotencyKey, ct);
                await _outbox.MarkDeliveredAsync(entry.Id, ct);
                return new NotificationDispatchResult(entry.Id, WasDeduplicated: false, NotificationDispatchStatus.Delivered);
            }

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

    /// <summary>
    /// W3-14 — the re-pointed leg: POST api/v1/sent-payload/user/{id} on push-notification.
    /// The caller's Idempotency-Key rides the request header VERBATIM (§4.1 rider) so the
    /// push service's own dispatch registry dedups on the same string the gateway used.
    /// </summary>
    private async Task SendUpstreamAsync(
        Guid entryId,
        string templateKey,
        string locale,
        RenderedNotification rendered,
        Dictionary<string, string> parameters,
        Guid recipientUserId,
        string? idempotencyKey,
        CancellationToken ct)
    {
        // Flat map: the relay copies every key but title/body into the FCM data map.
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in parameters) payload[kv.Key] = kv.Value;
        payload["title"] = rendered.Title;
        payload["body"] = rendered.Body;
        payload["type"] = templateKey;
        payload["language"] = locale;

        using (ServicePushNotificationClient.UseIdempotencyKey(idempotencyKey))
        {
            await _servicePush!.Send_notification_to_userAsync(
                recipientUserId.ToString(),
                new SentPayloadToUserRequest { Payload = payload },
                ct);
        }

        _logger.LogInformation(
            "Notification push dispatched to push-notification. EntryId={EntryId} TemplateKey={Template} "
            + "Recipient={UserId} IdempotencyKey={Key}",
            entryId, templateKey, recipientUserId, idempotencyKey);
    }
}
