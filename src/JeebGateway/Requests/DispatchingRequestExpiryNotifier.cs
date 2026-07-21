using JeebGateway.Services.Dispatch;
using Microsoft.Extensions.DependencyInjection;

namespace JeebGateway.Requests;

/// <summary>
/// Replaces the production registration of <see cref="InMemoryRequestExpiryNotifier"/>,
/// which only appended notifications to <see cref="List{T}"/> and meant no expiry push
/// ever reached a device. Deduplication is delegated to the dispatcher's
/// <c>idempotencyKey</c> contract, so no gateway state is needed.
/// </summary>
public sealed class DispatchingRequestExpiryNotifier : IRequestExpiryNotifier
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DispatchingRequestExpiryNotifier> _logger;

    public DispatchingRequestExpiryNotifier(
        IServiceScopeFactory scopeFactory,
        ILogger<DispatchingRequestExpiryNotifier> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task NotifyTryExpandTierAsync(
        string clientId,
        string requestId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        const string templateKey = "jeeb.request.try_expand_tier";
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
            var dispatcher = scope.ServiceProvider.GetRequiredService<IJeebNotificationDispatcher>();
            await dispatcher.DispatchAsync(
                templateKey,
                locale: "en",
                parameters: new Dictionary<string, string> { ["requestId"] = requestId },
                recipientUserId: uid,
                idempotencyKey: $"request-nudge:{requestId}",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Notification {TemplateKey} for request {RequestId} failed; the committed expiry state is unchanged.",
                templateKey,
                requestId);
        }
    }

    public async Task NotifyExpiredAsync(
        string clientId,
        string requestId,
        DateTimeOffset at,
        CancellationToken ct)
    {
        const string templateKey = "jeeb.request.expired";
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
            var dispatcher = scope.ServiceProvider.GetRequiredService<IJeebNotificationDispatcher>();
            await dispatcher.DispatchAsync(
                templateKey,
                locale: "en",
                parameters: new Dictionary<string, string> { ["requestId"] = requestId },
                recipientUserId: uid,
                idempotencyKey: $"request-expired:{requestId}",
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Notification {TemplateKey} for request {RequestId} failed; the committed expiry state is unchanged.",
                templateKey,
                requestId);
        }
    }
}
