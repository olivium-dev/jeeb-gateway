using System.Diagnostics;
using JeebGateway.NotificationPreferences;
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
///   4. Any transport exception schedules a single retry through
///      <see cref="IPushRetryQueue"/> 30 seconds out; retry-path delivery
///      reports <see cref="PushDeliveryOutcome.DeliveredOnRetry"/>, a second
///      failure is terminal and reported as <see cref="PushDeliveryOutcome.Failed"/>.
/// </summary>
public sealed class PushNotificationService : IPushNotificationService
{
    private readonly INotificationPreferencesStore _prefs;
    private readonly IDeviceTokenStore _devices;
    private readonly IReadOnlyDictionary<DevicePlatform, IPushTransport> _transports;
    private readonly IPushRetryQueue _retryQueue;
    private readonly IPushDeliveryTracker _tracker;
    private readonly PushOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<PushNotificationService> _log;

    public PushNotificationService(
        INotificationPreferencesStore prefs,
        IDeviceTokenStore devices,
        IEnumerable<IPushTransport> transports,
        IPushRetryQueue retryQueue,
        IPushDeliveryTracker tracker,
        IOptions<PushOptions> options,
        TimeProvider clock,
        ILogger<PushNotificationService> log)
    {
        _prefs = prefs;
        _devices = devices;
        _transports = transports.ToDictionary(t => t.Platform);
        _retryQueue = retryQueue;
        _tracker = tracker;
        _options = options.Value;
        _clock = clock;
        _log = log;
    }

    public async Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct)
    {
        var result = await SendInternalAsync(request, attempt: 1, ct);
        await _tracker.RecordAsync(result, ct);
        return result;
    }

    /// <summary>
    /// Used by <see cref="PushRetryQueueProcessor"/> to drive the retry path.
    /// The retry attempt MUST NOT re-enqueue on failure — the AC is "retried
    /// once", not "retried until success".
    /// </summary>
    internal async Task<PushDeliveryResult> SendForRetryAsync(PushNotificationRequest request, CancellationToken ct)
    {
        var result = await SendInternalAsync(request, attempt: 2, ct);
        await _tracker.RecordAsync(result, ct);
        return result;
    }

    private async Task<PushDeliveryResult> SendInternalAsync(PushNotificationRequest request, int attempt, CancellationToken ct)
    {
        var prefs = await _prefs.GetAsync(request.UserId, ct);
        if (!PushTriggerCategoryMap.IsAllowed(request.Trigger, prefs))
        {
            _log.LogInformation(
                "push suppressed for user {UserId}: trigger {Trigger} muted by user preference",
                request.UserId, request.Trigger);
            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.SuppressedByPreference, attempt - 1);
        }

        var devices = await _devices.GetForUserAsync(request.UserId, ct);
        if (devices.Count == 0)
        {
            _log.LogInformation(
                "push has no targets for user {UserId}: no devices registered for trigger {Trigger}",
                request.UserId, request.Trigger);
            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.NoDevices, attempt - 1);
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
                    "push timed out on {Platform} for user {UserId}, trigger {Trigger} (attempt {Attempt})",
                    device.Platform, request.UserId, request.Trigger, attempt);
            }
            catch (PushTransportException ex)
            {
                failures.Add($"{device.Platform}: {ex.Message}");
                _log.LogWarning(ex,
                    "push transport failed for user {UserId}, trigger {Trigger} (attempt {Attempt})",
                    request.UserId, request.Trigger, attempt);
            }
            catch (Exception ex)
            {
                failures.Add($"{device.Platform}: {ex.GetType().Name}");
                _log.LogError(ex,
                    "push unexpected failure for user {UserId}, trigger {Trigger} (attempt {Attempt})",
                    request.UserId, request.Trigger, attempt);
            }
        }

        sw.Stop();

        // Partial success counts as success — at least one device got the
        // push, the user saw it on at least one screen. We only retry when
        // every transport attempt failed (zero deliveries).
        if (delivered > 0)
        {
            if (sw.Elapsed > _options.DeliverySla)
            {
                _log.LogWarning(
                    "push for user {UserId} trigger {Trigger} exceeded {Sla}ms SLA ({Elapsed}ms) on attempt {Attempt}",
                    request.UserId, request.Trigger,
                    _options.DeliverySla.TotalMilliseconds, sw.Elapsed.TotalMilliseconds, attempt);
            }

            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                attempt == 1 ? PushDeliveryOutcome.Delivered : PushDeliveryOutcome.DeliveredOnRetry,
                attempt,
                failures.Count == 0 ? null : string.Join("; ", failures));
        }

        var reason = failures.Count == 0 ? "no transports attempted" : string.Join("; ", failures);

        if (attempt == 1)
        {
            var dueAt = _clock.GetUtcNow().Add(_options.RetryDelay);
            await _retryQueue.EnqueueAsync(new PushRetryEntry(request, dueAt, reason), ct);
            _log.LogInformation(
                "push first attempt failed for user {UserId} trigger {Trigger}; queued for retry at {DueAt} ({Reason})",
                request.UserId, request.Trigger, dueAt, reason);
            return new PushDeliveryResult(
                request.UserId, request.Trigger,
                PushDeliveryOutcome.QueuedForRetry, attempt, reason);
        }

        // Second attempt failure is terminal — AC is "retried once after 30 seconds".
        _log.LogError(
            "push retry failed for user {UserId} trigger {Trigger}: {Reason}",
            request.UserId, request.Trigger, reason);
        return new PushDeliveryResult(
            request.UserId, request.Trigger,
            PushDeliveryOutcome.Failed, attempt, reason);
    }
}
