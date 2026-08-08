using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Users;

/// <summary>
/// F3 guard 3 — matching reads presence, not roles (correction 6), so unregister must
/// force the jeeber offline through whichever path is authoritative (mirrors
/// AvailabilityController's own heart-beat/delivery-service branch). Best-effort.
/// </summary>
public interface IJeeberForceOfflineOnUnregister
{
    Task ForceOfflineAsync(string userId, CancellationToken ct);
}

public sealed class JeeberForceOfflineOnUnregister : IJeeberForceOfflineOnUnregister
{
    private readonly IAvailabilityStore _store;
    private readonly IDeliveryServiceClient _delivery;
    private readonly IHeartBeatServiceClient _heartBeat;
    private readonly IOptions<HeartbeatFeatureOptions> _heartbeatOptions;
    private readonly ILogger<JeeberForceOfflineOnUnregister> _log;

    public JeeberForceOfflineOnUnregister(
        IAvailabilityStore store,
        IDeliveryServiceClient delivery,
        IHeartBeatServiceClient heartBeat,
        IOptions<HeartbeatFeatureOptions> heartbeatOptions,
        ILogger<JeeberForceOfflineOnUnregister> log)
    {
        _store = store;
        _delivery = delivery;
        _heartBeat = heartBeat;
        _heartbeatOptions = heartbeatOptions;
        _log = log;
    }

    public async Task ForceOfflineAsync(string userId, CancellationToken ct)
    {
        try
        {
            if (_heartbeatOptions.Value.Enabled)
            {
                await _heartBeat.SetPresenceAsync(new HeartBeatPresenceRequest
                {
                    UserId = userId,
                    Online = false,
                    RoleKey = _heartbeatOptions.Value.RoleKey,
                }, ct);
            }
            else
            {
                await _delivery.SetAvailabilityAsync(
                    new JeeberAvailabilityUpstreamRequest { Online = false }, userId, ct);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "F3 unregister: force-offline upstream write failed for {UserId}; proceeding regardless.", userId);
        }

        try
        {
            await _store.GoOfflineAsync(userId, GoOfflineReason.UserToggle, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "F3 unregister: force-offline local mirror failed for {UserId}.", userId);
        }
    }
}
