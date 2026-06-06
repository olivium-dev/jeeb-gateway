namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the explicit public opt-out marker. An action carrying
/// <see cref="PublicEndpointAttribute"/> (alongside <c>[AllowAnonymous]</c>) declares that it is
/// public BY DESIGN and bypasses BOTH layers (Layer 1 audience + Layer 2 capability).
///
/// <para>This is the user-type analogue of ADR-004's authn opt-out: <c>[AllowAnonymous]</c> opts
/// out of Layer 1; <c>[PublicEndpoint]</c> opts out of Layer 2 and — critically — satisfies the
/// <see cref="CapabilityCoverageGuard"/> default-deny scan. The guard fails the build if a
/// non-public action has neither a <see cref="CapabilityRequirement"/> policy NOR this marker, so
/// no action is ever silently authorized for all user types by omission.</para>
///
/// <para>Public-by-design routes: token mint <c>/auth/tokens</c> (incl. super-login), OTP
/// request/verify/send/validate, auth refresh, <c>/health*</c> + internal/chat/notification health,
/// <c>/metrics</c>, swagger, <c>DevController</c> (config-gated dev seam), <c>/dev/seed</c>, KYC
/// form-schema + contract-template reads, FormBuilder reads, ProhibitedItems list, Tiers list,
/// <c>GatewayDbProbe</c>, and pre-auth login on the legacy <c>UserController</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class PublicEndpointAttribute : Attribute
{
    /// <summary>Optional human-readable reason this endpoint is public by design (for audit/Swagger).</summary>
    public string? Reason { get; init; }

    public PublicEndpointAttribute()
    {
    }

    public PublicEndpointAttribute(string reason)
    {
        Reason = reason;
    }
}
