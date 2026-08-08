namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Shared cache-key format for the <c>GET /v1/users/me</c> 30s profile cache
/// (<see cref="UsersMeController"/>). Any write path that changes what that
/// cache serves (role/switch, and now the profile PUTs on
/// <see cref="JeebGateway.Controllers.UserController"/>) must invalidate the
/// SAME key — extracted here so the format string cannot drift between the
/// reader and the writers (F5 validator correction: PUT /api/User/profile and
/// its twin /api/User/profile/update never removed this key, so a changed
/// avatar/name served stale for up to <c>ProfileCacheSeconds</c>).
/// </summary>
internal static class ProfileCacheKeys
{
    public static string ForUser(string userId) => $"v1:users:me:profile:{userId}";
}
