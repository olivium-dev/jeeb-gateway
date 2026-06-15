namespace JeebGateway.Push;

/// <summary>
/// S12 / JEB-62 — config-gated, DEFAULT-OFF test seams for the notification /
/// device-register negative cases (N2 / N5 / N6). Bound from the
/// <c>FeatureFlags:NotificationTestSeam</c> configuration section.
///
/// <para><b>Why a seam.</b> Three S12 negative steps assert failure modes that
/// cannot be produced through the normal happy path against the local fleet:
/// <list type="bullet">
///   <item><b>N2</b> — a genuinely role-less identity must be refused
///   (<c>400</c>, "no active role to route") rather than guessed into a group.
///   The gateway's local mint shell defaults every user to a <c>customer</c>
///   role, so an <c>active_role</c>-absent token never naturally occurs; the seam
///   makes an explicit <c>roles:[]</c> mint produce a TRULY role-less token so the
///   already-existing 400 path in <see cref="JeebDeviceController"/> fires
///   honestly.</item>
///   <item><b>N5</b> — push-relay down / register timeout must surface a typed
///   <c>503</c>. Requires fault injection (the relay is healthy locally).</item>
///   <item><b>N6</b> — register-ok-but-subscribe-partial must surface a
///   <c>207</c>. Requires fault injection.</item>
/// </list></para>
///
/// <para><b>Safety.</b> <see cref="Enabled"/> defaults to <c>false</c> and is
/// committed <c>false</c> in every appsettings file — production behaviour is
/// IDENTICAL with the seam off (the mint keeps its customer-shell fallback; the
/// device-register never honours a forced status). The seam is GENERIC: it injects
/// an arbitrary forced HTTP status from a request header, it does not hard-code any
/// scenario. It is enabled locally only via
/// <c>FeatureFlags__NotificationTestSeam__Enabled=true</c>.</para>
/// </summary>
public sealed class NotificationTestSeamOptions
{
    /// <summary>Configuration section: <c>FeatureFlags:NotificationTestSeam</c>.</summary>
    public const string SectionName = "FeatureFlags:NotificationTestSeam";

    /// <summary>
    /// Request header carrying a forced HTTP status code for device-register
    /// fault injection (N5 → 503, N6 → 207). Honoured ONLY when the seam is
    /// enabled. Absent header → normal behaviour.
    /// </summary>
    public const string ForceStatusHeader = "X-Test-Force-Register-Status";

    /// <summary>
    /// Master switch. Default <c>false</c> (fail-closed). When <c>false</c> every
    /// seam below is inert and the gateway behaves exactly as it does in production.
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// When <c>true</c> (AND <see cref="Enabled"/>), the token mint honours an
    /// EXPLICIT empty <c>roles:[]</c> request literally — it issues a role-less,
    /// active_role-less token instead of falling back to the profile's default
    /// <c>customer</c> role. Drives N2. Default <c>false</c>.
    /// </summary>
    public bool HonorExplicitEmptyRoles { get; set; } = false;

    /// <summary>
    /// When <c>true</c> (AND <see cref="Enabled"/>), the device-register endpoint
    /// honours the <see cref="ForceStatusHeader"/> request header and returns the
    /// forced status (N5 503 / N6 207) instead of the normal result. Default
    /// <c>false</c>.
    /// </summary>
    public bool HonorForceStatusHeader { get; set; } = false;
}
