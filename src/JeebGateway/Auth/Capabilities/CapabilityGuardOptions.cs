namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — options for the default-deny <see cref="CapabilityCoverageGuard"/>.
///
/// <para><b>One-shot build — FINAL step (enforce ON).</b> Every one of the ~46 controllers now
/// carries <c>[RequireCapability]</c> or <c>[PublicEndpoint]</c>, so <see cref="Enforce"/> defaults
/// to <c>true</c>: a newly added, un-annotated controller action fails the build/startup
/// (default-deny at the user-type layer — the analogue of ADR-004's authn FallbackPolicy). An
/// operator may set <c>CapabilityGuard:Enforce=false</c> as an emergency kill switch, but ENFORCE-ON
/// is the safe default.</para>
/// </summary>
public sealed class CapabilityGuardOptions
{
    public const string SectionName = "CapabilityGuard";

    /// <summary>
    /// When <c>true</c> (the ADR-005 FINAL default), the guard THROWS at startup if any non-public
    /// controller action lacks a <see cref="CapabilityRequirement"/> policy. When <c>false</c>
    /// (emergency override only), it logs warnings without failing the host.
    /// </summary>
    public bool Enforce { get; set; } = true;
}
