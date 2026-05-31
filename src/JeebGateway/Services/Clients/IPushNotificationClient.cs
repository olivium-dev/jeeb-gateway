namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the push-notification service (FastAPI, Postgres
/// <c>jeeb-push-notifications</c> / table <c>push_notification</c>). Used by
/// <c>PushController</c> when <c>FeatureFlags:UseUpstream:Push</c> is set, to
/// replace the in-memory <c>IDeviceTokenStore</c> on the device-register
/// write path.
///
/// The only call wired here is the device registration upsert — the "any call
/// that writes to the push DB". The upstream route is
/// <c>PUT {API_V1_STR}/register</c> (<c>PUT /api/v1/register</c>), which
/// performs an atomic upsert keyed on the <c>(device_id, user_id)</c> primary
/// key (see push-notification/app/endpoints/register_user.py::register).
///
/// Methods throw <see cref="HttpRequestException"/> on non-2xx so the
/// controller surfaces a ProblemDetails rather than silently swallowing the
/// failure.
/// </summary>
public interface IPushNotificationClient
{
    /// <summary>
    /// Registers (upserts) a device's FCM token for a user against the
    /// push-notification Postgres DB. Idempotent on (deviceId, userId).
    /// </summary>
    Task RegisterDeviceAsync(RegisterDeviceUpstreamRequest request, CancellationToken ct);
}

/// <summary>
/// Input to <see cref="IPushNotificationClient.RegisterDeviceAsync"/>. Maps
/// onto the upstream <c>RegisterRequest</c> (user_id, fcm_token, device_id,
/// optional active_role). The gateway's public device-register contract only
/// carries platform + token, so <see cref="DeviceId"/> is derived by the
/// controller and <see cref="ActiveRole"/> is left null (unknown at the BFF
/// layer; the upstream treats it as a backward-compatible optional field).
/// </summary>
public sealed class RegisterDeviceUpstreamRequest
{
    public required string UserId { get; init; }
    public required string FcmToken { get; init; }
    public required string DeviceId { get; init; }
    public string? ActiveRole { get; init; }
}
