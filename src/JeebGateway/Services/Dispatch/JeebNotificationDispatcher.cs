using JeebGateway.Migration;
using JeebGateway.Notifications;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Concrete implementation of <see cref="IJeebNotificationDispatcher"/>.
///
/// <para><b>Durability model.</b>  The outbox binding is mode-gated, never Postgres: in-memory today (MSI
/// sets no override) or StateServiceNotificationDispatchOutbox at NotificationOutboxMode=upstream-authority.</para>
///
/// <para><b>Push transport.</b>  Hands over to notification-service through
/// <see cref="IGenericEventDispatcher"/>; at upstream-authority it posts the push service
/// directly instead. The in-gateway push stack it used to call is deleted.</para>
/// </summary>
public sealed class JeebNotificationDispatcher : IJeebNotificationDispatcher
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    private readonly INotificationTemplateRenderer _renderer;
    private readonly IGenericEventDispatcher _events;
    private readonly INotificationDispatchOutbox _outbox;
    private readonly ILogger<JeebNotificationDispatcher> _logger;
    private readonly ServicePushNotificationClient? _servicePush;
    private readonly GwdbxMigrationPhase _phase;

    public JeebNotificationDispatcher(
        INotificationTemplateRenderer renderer,
        IGenericEventDispatcher events,
        INotificationDispatchOutbox outbox,
        ILogger<JeebNotificationDispatcher> logger,
        IOptions<GwdbxMigrationOptions>? migration = null,
        ServicePushNotificationClient? servicePush = null)
    {
        _renderer = renderer;
        _events = events;
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

        // W1-10: a claim-driven outbox is dispatched by WorkItemClaimWorker under a real lease.
        // Pushing here too would double-send, and complete/fail without a lease is a no-op.
        if (_outbox.IsClaimDriven)
        {
            // An unknown template stays a caller-visible 400 instead of a silent 202.
            return _renderer.Render(templateKey, locale, parameters) is null
                ? new NotificationDispatchResult(
                    entry.Id, WasDeduplicated: false, NotificationDispatchStatus.DLQ,
                    $"Unknown template key '{templateKey}'.")
                : new NotificationDispatchResult(
                    entry.Id, WasDeduplicated: false, NotificationDispatchStatus.Pending);
        }

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

            var data = new Dictionary<string, string>(parameters, StringComparer.Ordinal)
            {
                ["type"] = templateKey,
                ["language"] = locale,
            };

            // The entry's OWN idempotency key is the entity id; absent, the outbox entry id is.
            var classification = await PushHandover.DispatchAsync(
                _events, _logger, templateKey, recipientUserId.ToString(),
                string.IsNullOrWhiteSpace(idempotencyKey) ? entry.Id.ToString() : idempotencyKey!,
                rendered.Title, rendered.Body, data,
                PushSilencePolicy.CategoryForTemplateKey(templateKey) ?? PushSilencePolicy.CategoryDelivery,
                ct);

            if (!PushHandover.IsProducerOwned(classification))
            {
                var reason = $"notification-service did not own the hand-over ({classification}).";
                await _outbox.RecordFailureAsync(entry.Id, reason, MaxAttempts, RetryDelay, ct);
                return new NotificationDispatchResult(
                    entry.Id, WasDeduplicated: false, NotificationDispatchStatus.Pending, reason);
            }

            _logger.LogInformation(
                "Notification push dispatched. EntryId={EntryId} Outcome={Outcome}",
                entry.Id, classification);

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
