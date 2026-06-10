namespace JeebGateway.TestControlPlane;

/// <summary>
/// JEB-1502: Options for the test control-plane surface (<c>/__test/*</c>).
///
/// <para>
/// <b>Fail-closed to 404.</b> <see cref="Enabled"/> defaults to <c>false</c>
/// and is committed <c>false</c> in every appsettings file including
/// <c>appsettings.Production.json</c>. All <c>/__test/*</c> routes return 404
/// when off — indistinguishable from a non-existent route.
/// </para>
///
/// <para>
/// Defence in depth: even when enabled in-app, the nginx proxy prefix list
/// does not include <c>/__test</c> (prefixes are <c>/v1</c>, <c>/api</c>,
/// <c>/auth</c>, <c>/health</c>, <c>/metrics</c>, <c>/dev</c>, <c>/swagger</c>).
/// Requests from the internet never reach the gateway on this path. See WS-E
/// ingress smoke probe.
/// </para>
///
/// <para>
/// <b>Shared secret.</b> Every request to an enabled <c>/__test/*</c> route
/// MUST carry the <c>X-Test-Control-Plane-Secret</c> header matching
/// <see cref="SharedSecret"/>. A missing or wrong secret returns 401.
/// The secret is read from config/env; it is never committed.
/// </para>
/// </summary>
public sealed class TestControlPlaneOptions
{
    /// <summary>Configuration section: <c>TestControlPlane</c>.</summary>
    public const string SectionName = "TestControlPlane";

    /// <summary>
    /// Master switch. Default <c>false</c> (fail-closed). When <c>false</c>,
    /// every <c>/__test/*</c> route returns 404 regardless of the secret.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Shared-secret value required in the <c>X-Test-Control-Plane-Secret</c>
    /// header. When the plane is enabled and this is empty the plane refuses all
    /// requests with 401 (no-secret config is fail-closed too).
    /// </summary>
    public string SharedSecret { get; set; } = string.Empty;
}
