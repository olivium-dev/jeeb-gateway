using JeebGateway.Notifications;
using JeebGateway.Services.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Requests;

/// <summary>
/// Replaces the production registration of <see cref="InMemoryRequestExpiryNotifier"/>,
/// which only appended notifications to <see cref="List{T}"/> and meant no expiry push
/// ever reached a device. Commands are now durably accepted by notification-service,
/// which exclusively owns dispatch, retries, DLQ, and device resolution.
/// </summary>
public sealed class DispatchingRequestExpiryNotifier : IRequestExpiryNotifier
{
    // Was a seat-local 2s. Measured 2026-07-28, a push to a recipient who actually owns a
    // device costs 2.53-3.97s (10 consecutive calls), so 2s aborted every healthy send while
    // the "<200ms" figure it was sized from describes a recipient with NO device rows (404 in
    // ~14ms). Shared value now — see PushSendBudget for the distribution and the bound.
    private static readonly TimeSpan PushTimeout = JeebGateway.Notifications.PushSendBudget.PerRecipient;

    /// <summary>
    /// The gateway bounds its owner-acceptance call. A timeout is safe to replay:
    /// the stable notification UUID makes an ambiguous accept idempotent, while
    /// notification-service owns all provider retry attempts after persistence.
    /// </summary>

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

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);
            var owner = scope.ServiceProvider.GetRequiredService<INotificationOwnerClient>();
            await owner.PublishAsync(
                new NotificationOwnerEvent(
                    NotificationOwnerEventId.FromIdempotencyKey(idempotencyKey),
                    uid.ToString("D"),
                    rendered.Title,
                    rendered.Body,
                    $"gateway.{notificationType}",
                    payload,
                    locale),
                cts.Token);
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

}
