namespace JeebGateway.Push;

/// <summary>
/// One delivery channel — FCM for Android/web, APNs for iOS. Production
/// wiring talks to Google FCM HTTP v1 and Apple APNs HTTP/2 respectively;
/// the in-memory variant records every attempt so integration tests can
/// assert which transport fired, with what payload, against which token.
///
/// Implementations MUST be cancellation-aware so the unified
/// <see cref="IPushNotificationService"/> can enforce its 5-second
/// delivery SLA via a per-attempt linked CTS.
/// </summary>
public interface IPushTransport
{
    /// <summary>The platform this transport handles. Used by the dispatcher to route.</summary>
    DevicePlatform Platform { get; }

    /// <summary>Hand one notification to the underlying provider. Throw on transient failure.</summary>
    Task SendAsync(DeviceToken device, PushNotificationRequest request, CancellationToken ct);
}

public sealed class PushTransportException : Exception
{
    public PushTransportException(string message) : base(message) { }
    public PushTransportException(string message, Exception inner) : base(message, inner) { }
}
