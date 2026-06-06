namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — options for the default-deny <see cref="CapabilityCoverageGuard"/>.
///
/// <para><b>One-shot build staging.</b> In the L2 CORE step the guard is PRESENT but
/// <see cref="Enforce"/> defaults to <c>false</c>, so it logs uncovered actions as warnings
/// without failing the host (the ~46 controllers are not yet annotated). The FINAL step of the
/// one-shot build — after every controller carries <c>[RequireCapability]</c> or
/// <c>[PublicEndpoint]</c> — flips <c>CapabilityGuard:Enforce=true</c> so a newly added,
/// un-annotated action fails the build/startup (default-deny at the user-type layer).</para>
/// </summary>
public sealed class CapabilityGuardOptions
{
    public const string SectionName = "CapabilityGuard";

    /// <summary>
    /// When <c>true</c>, the guard THROWS at startup if any non-public controller action lacks a
    /// <see cref="CapabilityRequirement"/> policy. When <c>false</c> (default for the CORE step),
    /// it only logs warnings. The final annotation step sets this to <c>true</c>.
    /// </summary>
    public bool Enforce { get; set; }
}
