using System.Collections.Concurrent;

namespace JeebGateway.Push;

/// <summary>
/// In-memory transport used by integration tests and the early-MVP local
/// runs. Production swap will replace this binding for the two
/// <see cref="DevicePlatform"/> values with a real FCM HTTP v1 client and
/// a real APNs HTTP/2 client (an NSwag-generated client against the
/// notification-service surface, per the BFF aggregation pattern).
///
/// Two operating modes:
///   * Default — records every successful attempt in <see cref="Sent"/>.
///   * <see cref="FailNext"/> — the configured number of next attempts
///     throw <see cref="PushTransportException"/>. Used to exercise the
///     30-second retry path (AC: "Failed notifications retried once after
///     30 seconds").
/// </summary>
public sealed class InMemoryPushTransport : IPushTransport
{
    private readonly ConcurrentQueue<SentRecord> _sent = new();
    private readonly object _lock = new();
    private int _failuresPending;

    public InMemoryPushTransport(DevicePlatform platform)
    {
        Platform = platform;
    }

    public DevicePlatform Platform { get; }

    /// <summary>Cause the next N <see cref="SendAsync"/> calls to throw.</summary>
    public void FailNext(int count)
    {
        lock (_lock) _failuresPending = count;
    }

    public IReadOnlyList<SentRecord> Sent => _sent.ToArray();

    public Task SendAsync(DeviceToken device, PushNotificationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lock)
        {
            if (_failuresPending > 0)
            {
                _failuresPending--;
                throw new PushTransportException(
                    $"InMemoryPushTransport[{Platform}] injected failure for user {device.UserId}");
            }
        }

        _sent.Enqueue(new SentRecord(device, request, DateTimeOffset.UtcNow));
        return Task.CompletedTask;
    }

    public sealed record SentRecord(DeviceToken Device, PushNotificationRequest Request, DateTimeOffset At);
}
