namespace JeebGateway.Auth.SuperLogin;

/// <summary>
/// Config-bound roster for the debug-only Super-Login+ picker
/// (<c>GET /api/User/demo-users</c>). The roster — including each row's
/// <c>passcode</c> (the real SuperAdmin passcode the gateway forwards to
/// user-management on <c>/api/User/user-id-login</c>) — is supplied entirely
/// from configuration (env <c>DemoUsers__Users__N__*</c> or a JSON blob), never
/// hardcoded in source. When the roster is empty the endpoint returns an empty
/// <c>{ users: [] }</c> (the picker then shows an empty list, not a hard error).
/// </summary>
public sealed class DemoUsersOptions
{
    public const string SectionName = "DemoUsers";

    /// <summary>
    /// When false the <c>/api/User/demo-users</c> endpoint returns 404 (the
    /// surface is fully disabled). Default true so a configured roster is served.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public List<DemoUserRow> Users { get; set; } = new();
}

public sealed class DemoUserRow
{
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Role { get; set; } = "client";
    public string Passcode { get; set; } = string.Empty;
}
