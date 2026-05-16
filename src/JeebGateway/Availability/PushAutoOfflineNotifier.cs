using JeebGateway.Push;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Availability;

/// <summary>
/// Production wiring for <see cref="IAutoOfflineNotifier"/>: hands the
/// auto-offline event to the shared <see cref="IPushNotificationService"/>
/// so it goes through the same preference/transport/retry pipeline as
/// every other push trigger (T-backend-022 + T-backend-023).
/// </summary>
public class PushAutoOfflineNotifier : IAutoOfflineNotifier
{
    private readonly IPushNotificationService _push;
    private readonly ILogger<PushAutoOfflineNotifier> _logger;

    public PushAutoOfflineNotifier(IPushNotificationService push, ILogger<PushAutoOfflineNotifier> logger)
    {
        _push = push;
        _logger = logger;
    }

    public async Task NotifyAutoOfflineAsync(string userId, DateTimeOffset at, CancellationToken ct)
    {
        var request = new PushNotificationRequest(
            UserId: userId,
            Trigger: NotificationTrigger.AutoOffline,
            Title: "You're now offline",
            Body: "We set you offline after 30 minutes of inactivity. Toggle online to start receiving offers again.",
            Data: new Dictionary<string, string>
            {
                ["reason"] = "auto_offline_inactive",
                ["at"] = at.ToString("O")
            },
            IdempotencyKey: $"auto-offline:{userId}:{at:yyyyMMddTHHmm}");

        try
        {
            await _push.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Auto-offline push failed for {UserId}", userId);
        }
    }
}
