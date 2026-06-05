namespace JeebGateway.Security;

/// <summary>
/// Strongly-typed flag for the additive, env-gated Swagger UI / OpenAPI surface
/// (<c>/swagger</c>, <c>/swagger/v1/swagger.json</c>) on the live gateway.
///
/// Bound from configuration section <c>Features:Swagger</c> (mirrors the existing
/// <see cref="DevEndpointOptions"/> / <c>Features:DevEndpoints</c> options pattern
/// in <c>Program.cs</c>).
///
/// <para>
/// <b>Fail-closed to 404, admin-gated when on.</b> <see cref="Enabled"/> defaults
/// to <c>false</c> and is committed <c>false</c> in EVERY appsettings file —
/// including <c>appsettings.Production.json</c>. While off, <c>/swagger*</c>
/// returns 404, so the production surface is indistinguishable from "no such
/// endpoint". The flag is opted ON only via the environment variable
/// <c>Features__Swagger__Enabled=true</c> — applied exclusively by the
/// <c>deploy-to-jeeb.yml</c> <c>swagger_ui</c> input — never committed
/// <c>true</c>.
/// </para>
///
/// <para>
/// <b>Security (JEB / C15).</b> <c>jeeb.fds-1.com</c> is a PUBLIC host. Swagger
/// exposes the full gateway route surface and request/response schemas — an
/// information-disclosure / reconnaissance risk. So when the flag is ON under
/// Production it does NOT mount the open Development/Testing branch (which has no
/// auth gate). Instead it mounts a <c>UseWhen</c> branch over <c>/swagger</c>
/// that returns 404 unless the caller presents an authenticated principal in the
/// <c>admin</c> role (admin =&gt; 200, non-admin =&gt; 404). This is the same
/// admin gate that previously keyed on the <c>Staging</c> environment, re-keyed
/// onto this flag so it runs under the live Production host.
/// </para>
/// </summary>
public sealed class SwaggerOptions
{
    /// <summary>
    /// Configuration section this options class binds from.
    /// Bound in <c>Program.cs</c> via
    /// <c>Configuration.GetSection("Features").GetSection("Swagger")</c>.
    /// </summary>
    public const string SectionName = "Features:Swagger";

    /// <summary>
    /// Master switch for the admin-gated <c>/swagger*</c> surface under
    /// Production. Default <c>false</c> (fail-closed). When <c>false</c>,
    /// <c>/swagger*</c> returns 404. When <c>true</c>, <c>/swagger*</c> is mounted
    /// behind the admin-role gate (non-admin =&gt; 404).
    /// </summary>
    public bool Enabled { get; set; } = false;
}
