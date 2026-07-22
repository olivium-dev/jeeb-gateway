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
    private static readonly TimeSpan PushTimeout = TimeSpan.FromSeconds(2);

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

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PushTimeout);

            var push = scope.ServiceProvider.GetRequiredService<ServicePushNotificationClient>();
            await push.Send_notification_to_userAsync(
                clientId,
                new SentPayloadToUserRequest { Payload = payload },
                cts.Token);

            // Preserve the existing request-expired/request-nudge deduplication without
            // routing delivery back through the in-gateway device-token registry.
            var entry = await outbox.AddAsync(new NotificationDispatchEntry
            {
                TemplateKey = templateKey,
                Locale = locale,
                Parameters = parameters,
                RecipientUserId = uid,
                IdempotencyKey = idempotencyKey,
            }, ct);
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
}
