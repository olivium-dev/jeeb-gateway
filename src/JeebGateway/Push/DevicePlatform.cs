namespace JeebGateway.Push;

/// <summary>
/// The two push platforms supported in the MVP. Web push and Huawei HMS are
/// intentionally out of scope for T-backend-022.
/// </summary>
public enum DevicePlatform
{
    Fcm,
    Apns
}
