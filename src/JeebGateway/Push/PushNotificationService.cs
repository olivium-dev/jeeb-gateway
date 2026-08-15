using System.Diagnostics;
using JeebGateway.NotificationPreferences;
using JeebGateway.Users;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Push;

/// <summary>
/// Reference implementation for T-backend-022. Sequencing per push:
///
///   1. Resolve the user's preferences via <see cref="INotificationPreferencesStore"/>;
///      a muted category short-circuits with <see cref="PushDeliveryOutcome.SuppressedByPreference"/>.
///      Always-on triggers (KYC, OTP) bypass this check.
///   2. Resolve registered devices via <see cref="IDeviceTokenStore"/>; no
///      devices yields <see cref="PushDeliveryOutcome.NoDevices"/>.
///   3. Fan out to the platform-matched <see cref="IPushTransport"/> for
///      every device under a single per-attempt CTS bounded by
///      <see cref="PushOptions.DeliverySla"/>.
///   4. A fan-out where every transport fails is terminal and reported as
///      <see cref="PushDeliveryOutcome.Failed"/> (retry rail deleted, W5 retire-4).
/// </summary>
public sealed class PushNotificationService : IPushNotificationService
{
    private readonly INotificationPreferencesStore _prefs;
    private readonly IDeviceTokenStore _devices;
    private readonly IReadOnlyDictionary<DevicePlatform, IPushTransport> _transports;
    private readonly IPushDeliveryTracker _tracker;
    private readonly IUsersStore _users;
    private readonly PushOptions _options;
    private readonly ILogger<PushNotificationService> _log;

    public PushNotificationService(
        INotificationPreferencesStore prefs,
        IDeviceTokenStore devices,
        IEnumerable<IPushTransport> transports,
        IPushDeliveryTracker tracker,
        IUsersStore users,
        IOptions<PushOptions> options,
        ILogger<PushNotificationService> log)
    {
        _prefs = prefs;
        _devices = devices;
        _transports = transports.ToDictionary(t => t.Platform);
        _tracker = tracker;
        _users = users;
        _options = options.Value;
        _log = log;
    }

    public async Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
    {
        var enriched = await ResolveLanguageAsync(request, ct);
        var result = await SendInternalAsync(enriched, ct);
        await _tracker.RecordAsync(result, ct);
        return result;
    }

    /// <summary>
    /// T-backend-029 AC #6. When the caller didn't pre-localise the payload,
    /// stamp the request with the recipient's persisted language so transports
    /// (and any downstream renderer) can carry the correct locale through to
    /// the device. A missing user row falls back to the request as-is — push
    /// is best-effort; we don't want a profile-lookup miss to suppress a KYC
    /// or OTP delivery.
    /// </summary>
    private async Task<PushNotificationRequest> ResolveLanguageAsync(PushNotificationRequest request, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(request.Language)) return request;

        var profile = await _users.GetByIdAsync(request.UserId, ct);
        if (profile is null || string.IsNullOrEmpty(profile.Language)) return request;

        return request with { Language = profile.Language };
    }

    private async Task<PushDeliveryResult> SendInternalAsync(PushNotificationRequest request, CancellationToken ct)
    {
        UserNotificationPreferences prefs;
        try
        {
            prefs = await _prefs.GetAsync(request.UserId, ct);
        }
        catch (UserPreferencesUnavailableException ex)
        {
            // Push stays best-effort: an unreachable preferences store must not suppress
            // delivery. The fail-open now lives HERE, at the caller that wants it.
            _log.LogWarning(ex,
                "push preferences unavailable for user {UserId}; proceeding on defaults for trigger {Trigger}",
                request.UserId, request.Trigger);
            prefs = NotificationPreferencesDefaults.NewDefault(request.UserId);
        }

        if (!PushTriggerCategoryMap.IsAllowed(request.Trigger, prefs))
        {
            _log.LogInformation(
                "push suppressed for user {UserId}: trigger {Trigger} muted by user preference",
                request.UserId, request.Trigger);
            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.SuppressedByPreference, 0);
        }

        var devices = await _devices.GetForUserAsync(request.UserId, ct);
        if (devices.Count == 0)
        {
            _log.LogInformation(
                "push has no targets for user {UserId}: no devices registered for trigger {Trigger}",
                request.UserId, request.Trigger);
            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.NoDevices, 0);
        }

        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // The DeliverySla is the upper bound on the whole fan-out for the
        // 5-second SLO; per-transport timeout is still applied separately.
        attemptCts.CancelAfter(_options.DeliverySla);

        var sw = Stopwatch.StartNew();
        var failures = new List<string>();
        var delivered = 0;

        foreach (var device in devices)
        {
            if (!_transports.TryGetValue(device.Platform, out var transport))
            {
                failures.Add($"no transport for platform {device.Platform}");
                continue;
            }

            try
            {
                using var perTransportCts = CancellationTokenSource.CreateLinkedTokenSource(attemptCts.Token);
                perTransportCts.CancelAfter(_options.TransportTimeout);
                await transport.SendAsync(device, request, perTransportCts.Token);
                delivered++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                failures.Add($"{device.Platform} timed out");
                _log.LogWarning(ex,
                    "push timed out on {Platform} for user {UserId}, trigger {Trigger}",
                    device.Platform, request.UserId, request.Trigger);
            }
            catch (PushTransportException ex)
            {
                failures.Add($"{device.Platform}: {ex.Message}");
                _log.LogWarning(ex,
                    "push transport failed for user {UserId}, trigger {Trigger}",
                    request.UserId, request.Trigger);
            }
            catch (Exception ex)
            {
                failures.Add($"{device.Platform}: {ex.GetType().Name}");
                _log.LogError(ex,
                    "push unexpected failure for user {UserId}, trigger {Trigger}",
                    request.UserId, request.Trigger);
            }
        }

        sw.Stop();

        // Partial success counts as success — at least one device got the
        // push; a fan-out where every transport failed is terminal.
        if (delivered > 0)
        {
            if (sw.Elapsed > _options.DeliverySla)
            {
                _log.LogWarning(
                    "push for user {UserId} trigger {Trigger} exceeded {Sla}ms SLA ({Elapsed}ms)",
                    request.UserId, request.Trigger,
                    _options.DeliverySla.TotalMilliseconds, sw.Elapsed.TotalMilliseconds);
            }

            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.Delivered,
                1,
                failures.Count == 0 ? null : string.Join("; ", failures));
        }

        var reason = failures.Count == 0 ? "no transports attempted" : string.Join("; ", failures);

        // Retry rail deleted (W5 retire-4): a fully failed fan-out is terminal.
        _log.LogError(
            "push delivery failed for user {UserId} trigger {Trigger}: {Reason}",
            request.UserId, request.Trigger, reason);
        return new PushDeliveryResult(
            request.UserId, request.Trigger,
            PushDeliveryOutcome.Failed, 1, reason);
    }
}
