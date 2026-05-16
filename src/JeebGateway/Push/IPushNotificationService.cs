namespace JeebGateway.Push;

/// <summary>
/// Unified outbound surface for every push-eligible event in jeeb-gateway
/// (T-backend-022). One in-process call here replaces the ad-hoc per-trigger
/// notifiers — preference filtering, device token resolution, transport
/// selection (FCM/APNs), and the single 30-second retry are all centralised.
/// </summary>
public interface IPushNotificationService
{
    Task<PushDeliveryResult> SendAsync(PushNotificationRequest request, CancellationToken ct);
}
