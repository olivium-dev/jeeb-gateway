using JeebGateway.Services.Dispatch;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Requests;

/// <summary>
/// Replaces the production registration of <see cref="InMemoryRequestExpiryNotifier"/>,
/// which only appended notifications to <see cref="List{T}"/> and meant no expiry push
/// ever reached a device. Notifications use the external push-service registry where
/// the mobile app registers its tokens; the in-gateway device-token registry is not used.
/// </summary>
public sealed class DispatchingRequestExpiryNotifier : IRequestExpiryNotifier
{
    // Was a seat-local 2s. Measured 2026-07-28, a push to a recipient who actually owns a
    // device costs 2.53-3.97s (10 consecutive calls), so 2s aborted every healthy send while
    // the "<200ms" figure it was sized from describes a recipient with NO device rows (404 in
    // ~14ms). Shared value now — see PushSendBudget for the distribution and the bound.
    private static readonly TimeSpan PushTimeout = JeebGateway.Notifications.PushSendBudget.PerRecipient;

    /// <summary>
    /// A request nudge / expiry push is a FIRE-ONCE heuristic, so the reserved
    /// outbox row is allowed exactly one dispatch attempt. Nothing in the gateway
    /// re-drives <see cref="INotificationDispatchOutbox.GetDueAsync"/> for this
    /// path (the sweeper itself short-circuits on the idempotency key), so leaving
    /// a failed entry <c>Pending</c> would claim a retry that never happens.
    /// One attempt then straight to the DLQ, where
    /// <c>GET /v1/notifications/dlq</c> surfaces it with its last error.
    /// </summary>
    private const int MaxDispatchAttempts = 1;

    /// <summary>Upper bound on the error text persisted to <c>last_error</c>.</summary>
    private const int MaxRecordedErrorLength = 500;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DispatchingRequestExpiryNotifier> _logger;

    public DispatchingRequestExpiryNotifier(
        IServiceScopeFactory scopeFactory,
        ILogger<DispatchingRequestExpiryNotifier> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public Task NotifyTryExpandTierAsync(
        string clientId,
        string requestId,
        DateTimeOffset at,
        CancellationToken ct)
        => NotifyAsync(
            clientId,
            requestId,
            templateKey: "jeeb.request.try_expand_tier",
            notificationType: "try_expand_tier",
            idempotencyKey: $"request-nudge:{requestId}",
            ct);

    public Task NotifyExpiredAsync(
        string clientId,
        string requestId,
        DateTimeOffset at,
        CancellationToken ct)
        => NotifyAsync(
            clientId,
            requestId,
            templateKey: "jeeb.request.expired",
            notificationType: "request_expired",
            idempotencyKey: $"request-expired:{requestId}",
            ct);

    private async Task NotifyAsync(
        string clientId,
        string requestId,
        string templateKey,
        string notificationType,
        string idempotencyKey,
        CancellationToken ct)
    {
        if (!Guid.TryParse(clientId, out var uid))
        {
            _logger.LogWarning(
                "Skipping notification {TemplateKey} for request {RequestId}: client ID is not a valid GUID.",
                templateKey,
                requestId);
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var outbox = scope.ServiceProvider.GetRequiredService<INotificationDispatchOutbox>();
            if (await outbox.ExistsAsync(idempotencyKey, ct))
            {
                return;
            }

            var renderer = scope.ServiceProvider.GetRequiredService<INotificationTemplateRenderer>();
            var parameters = new Dictionary<string, string> { ["requestId"] = requestId };
            // TODO: use the customer's locale when the external push path exposes it cheaply.
            const string locale = "en";
            var rendered = renderer.Render(templateKey, locale, parameters);
            if (rendered is null)
            {
                _logger.LogWarning(
                    "Skipping notification {TemplateKey} for request {RequestId}: template was not found.",
                    templateKey,
                    requestId);
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["title"] = rendered.Title,
                ["body"] = rendered.Body,
                ["type"] = notificationType,
                ["category"] = "delivery",
                ["requestId"] = requestId,
                ["request_id"] = requestId,
                ["language"] = locale,
            };

            // RESERVE the dedupe row BEFORE the push attempt.
            //
            // This row used to be written only AFTER a successful send, with the
            // failure swallowed by the catch below. A push that kept failing
            // (e.g. the push service 500s because every stored device token for
            // this user is dead) therefore never recorded `request-nudge:{id}`,
            // so ExistsAsync above never deduplicated and RequestNudgeSweeper
            // re-sent the identical nudge on EVERY 30s sweep, forever — hundreds
            // of calls per stuck request, which in turn kept the shared push
            // client's circuit breaker pinned open for unrelated users.
            //
            // The nudge/expiry push is a fire-once heuristic: the row is the
            // record that we attempted it. Losing one to a genuine transient
            // failure is the deliberate trade-off against an unbounded re-send
            // loop; the attempt is not lost silently — a failed attempt lands in
            // the DLQ with its last error (see MaxDispatchAttempts).
            var entry = await outbox.AddAsync(new NotificationDispatchEntry
            {
                TemplateKey = templateKey,
                Locale = locale,
                Parameters = parameters,
                RecipientUserId = uid,
                IdempotencyKey = idempotencyKey,
            }, ct);

            try
            {
                // Expiry/nudge has no notification-centre route; the generic seam carries it
                // once the gateway stops producing pushes directly.
                var events = scope.ServiceProvider
                    .GetService<JeebGateway.Notifications.IGenericEventDispatcher>()
                    ?? JeebGateway.Notifications.NullGenericEventDispatcher.Instance;
                var handover = await events.DispatchAsync(
                    JeebGateway.Notifications.JeebGenericEventTypes.RequestExpiringEventType,
                    clientId,
                    $"{notificationType}:{requestId}",
                    rendered.Title,
                    rendered.Body,
                    payload.ToDictionary(
                        kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty, StringComparer.Ordinal),
                    JeebGateway.Notifications.PushSilencePolicy.CategoryRequestExpired,
                    ct);

                if (handover.Classification
                    == JeebGateway.Notifications.GenericEventDispatchClassification
                        .SkippedDirectDispatchArmed)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(PushTimeout);

                    var push = scope.ServiceProvider.GetRequiredService<ServicePushNotificationClient>();
                    await push.Send_notification_to_userAsync(
                        clientId,
                        new SentPayloadToUserRequest { Payload = payload },
                        cts.Token);
                }
                else if (handover.Classification
                    == JeebGateway.Notifications.GenericEventDispatchClassification.Unproven)
                {
                    throw new InvalidOperationException(
                        "Generic event hand-over for the request expiry notification was unproven.");
                }
            }
            catch (Exception pushEx)
            {
                _logger.LogWarning(
                    pushEx,
                    "Notification {TemplateKey} for request {RequestId} failed to dispatch; " +
                    "the outbox entry is recorded as failed and will NOT be re-sent.",
                    templateKey,
                    requestId);
                await RecordDispatchFailureAsync(outbox, entry.Id, pushEx, templateKey, requestId, ct);
                return;
            }

            await outbox.MarkDeliveredAsync(entry.Id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Notification {TemplateKey} for request {RequestId} failed; request lifecycle processing continues.",
                templateKey,
                requestId);
        }
    }

    /// <summary>
    /// Books the failed dispatch against the reserved outbox entry so the attempt
    /// is observable (attempt count + last error, then DLQ) instead of vanishing.
    /// Bookkeeping is itself best-effort: if the outbox write fails, the reserved
    /// row still exists and still deduplicates, which is the property that stops
    /// the re-send loop.
    /// </summary>
    private async Task RecordDispatchFailureAsync(
        INotificationDispatchOutbox outbox,
        Guid entryId,
        Exception pushEx,
        string templateKey,
        string requestId,
        CancellationToken ct)
    {
        try
        {
            var error = $"{pushEx.GetType().Name}: {pushEx.Message}";
            if (error.Length > MaxRecordedErrorLength)
            {
                error = error[..MaxRecordedErrorLength];
            }

            await outbox.RecordFailureAsync(
                entryId,
                error,
                maxAttempts: MaxDispatchAttempts,
                retryDelay: TimeSpan.Zero,
                ct);
        }
        catch (Exception bookkeepingEx)
        {
            _logger.LogWarning(
                bookkeepingEx,
                "Could not record the failed dispatch of {TemplateKey} for request {RequestId}; " +
                "the reserved outbox entry still suppresses a re-send.",
                templateKey,
                requestId);
        }
    }
}
