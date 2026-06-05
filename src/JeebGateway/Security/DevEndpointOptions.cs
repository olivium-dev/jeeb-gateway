namespace JeebGateway.Security;

/// <summary>
/// Strongly-typed flag for the additive, env-gated developer/test-harness
/// endpoints under <c>/dev/*</c> (see <see cref="DevOnlyAttribute"/> and
/// <see cref="JeebGateway.Controllers.DevController"/>).
///
/// Bound from configuration section <c>Features:DevEndpoints</c> (mirrors the
/// existing <see cref="SecurityOptions"/> / <c>UpstreamFeatureFlags</c> options
/// pattern in <c>Program.cs</c>).
///
/// <para>
/// <b>Fail-closed to 404.</b> <see cref="Enabled"/> defaults to
/// <c>false</c> and is committed <c>false</c> in EVERY appsettings file —
/// including <c>appsettings.Production.json</c>. While off, every <c>/dev/*</c>
/// route behaves as if it does not exist (HTTP 404), so the production surface
/// is indistinguishable from "no such endpoint". The flag is opted ON only via
/// the environment variable <c>Features__DevEndpoints__Enabled=true</c> in the
/// single environment where an external test harness drives seeding — it is
/// never committed <c>true</c>.
/// </para>
///
/// <para>
/// These endpoints exist ONLY so an external testing tool can create REAL
/// user-management users on demand and inspect them. The gateway NEVER seeds
/// data automatically: there is no startup hook, no <c>IHostedService</c>, no
/// background sweeper, and no migration that seeds. Seeding happens only when an
/// explicit HTTP call hits <c>POST /dev/seed/user</c>.
/// </para>
/// </summary>
public sealed class DevEndpointOptions
{
    /// <summary>
    /// Configuration section this options class binds from.
    /// Bound in <c>Program.cs</c> via
    /// <c>Configuration.GetSection("Features").GetSection("DevEndpoints")</c>.
    /// </summary>
    public const string SectionName = "Features:DevEndpoints";

    /// <summary>
    /// Master switch for the <c>/dev/*</c> routes. Default <c>false</c>
    /// (fail-closed). When <c>false</c>, every dev route returns 404.
    /// </summary>
    public bool Enabled { get; set; } = false;
}
