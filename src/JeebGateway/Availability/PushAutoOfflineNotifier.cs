using JeebGateway.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Availability;

/// <summary>
/// Production wiring for <see cref="IAutoOfflineNotifier"/>: hands the auto-offline event to
/// notification-service. Was on the deleted in-gateway stack, so it delivered nothing.
/// </summary>
public class PushAutoOfflineNotifier : IAutoOfflineNotifier
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PushAutoOfflineNotifier> _logger;

    // SINGLETON consuming a SCOPED dispatcher: open a fresh scope per notify, never capture it.
    public PushAutoOfflineNotifier(IServiceScopeFactory scopes, ILogger<PushAutoOfflineNotifier> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public async Task NotifyAutoOfflineAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "availability",
            ["reason"] = "auto_offline_inactive",
            ["at"] = at.ToString("O")
        };

        using var scope = _scopes.CreateScope();
        var events = scope.ServiceProvider.GetRequiredService<IGenericEventDispatcher>();

        // The pre-existing idempotency key IS the entity id; no new key is minted.
        await PushHandover.DispatchAsync(
            events,
            _logger,
            JeebGenericEventTypes.AutoOfflineEventType,
            userId,
            $"auto-offline:{userId}:{at:yyyyMMddTHHmm}",
            "You're now offline",
            "We set you offline after 30 minutes of inactivity. Toggle online to start receiving offers again.",
            data,
            PushSilencePolicy.CategoryAvailability,
            ct);
    }
}
