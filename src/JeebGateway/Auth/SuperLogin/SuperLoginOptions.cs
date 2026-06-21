namespace JeebGateway.Auth.SuperLogin;

/// <summary>
/// Production-safety switch for the privileged "super-login" demo surfaces.
///
/// <para><b>What it gates.</b> Two endpoints exist purely to support the
/// debug Super-Login+ demo flow and are unsafe to leave open in production:</para>
/// <list type="bullet">
///   <item><c>POST /auth/tokens</c> — the credential-less token mint. When the
///   open mode is on, the gateway will mint a session JWT for an arbitrary
///   <c>userId</c> with no service credential (account-takeover class). The mint
///   is otherwise gated by the <c>X-Service-Auth-Key</c> header
///   (see <c>Security:TokenMint</c>).</item>
///   <item><c>GET /api/User/demo-users</c> — the anonymous demo-user picker
///   roster (each row carries the real SuperAdmin passcode). In production the
///   roster must not be enumerable anonymously.</item>
/// </list>
///
/// <para><b>Default is SAFE.</b> <see cref="OpenMode"/> defaults to
/// <c>false</c>, so an out-of-the-box (flag-unset) deploy is gated: the token
/// mint REQUIRES the service key and the demo-user picker is disabled (404).
/// The MSI demo environment sets <c>SuperLogin__OpenMode=true</c> to preserve
/// the current open demo behavior — that single env flag, and nothing in code,
/// is what keeps the demo unchanged.</para>
///
/// <para>When <see cref="OpenMode"/> is <c>true</c> it ALSO forces the legacy
/// <c>Security:TokenMint:Enabled</c> gate off (so the mint is open) and the
/// <c>DemoUsers</c> roster on, regardless of those sections — making this the
/// single authoritative demo switch.</para>
/// </summary>
public sealed class SuperLoginOptions
{
    public const string SectionName = "SuperLogin";

    /// <summary>
    /// Master demo switch. <c>false</c> (the default) = PROD-SAFE: the token
    /// mint is service-key gated and the anonymous demo-user picker is disabled.
    /// <c>true</c> = DEMO: the token mint is open (no service key) and the
    /// anonymous demo-user picker is served. Set via env
    /// <c>SuperLogin__OpenMode</c>.
    /// </summary>
    public bool OpenMode { get; set; } = false;
}
