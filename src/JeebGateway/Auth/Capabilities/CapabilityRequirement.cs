using Microsoft.AspNetCore.Authorization;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the authorization requirement carried by each per-capability policy.
/// One <see cref="CapabilityRequirement"/> is registered per capability at startup
/// (<c>cap:&lt;capability&gt;</c> policy) and satisfied by <see cref="CapabilityAuthorizationHandler"/>.
///
/// <para>This requirement is the user-TYPE (Layer 2) decision and is intentionally distinct
/// from <see cref="JeebGateway.Auth.GatewayAudienceRequirement"/> (Layer 1, caller validity).
/// Layer 1 failure → 401; this (Layer 2) failure on an authenticated caller → 403.</para>
/// </summary>
public sealed class CapabilityRequirement : IAuthorizationRequirement
{
    public CapabilityRequirement(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new ArgumentException("Capability must be a non-empty name.", nameof(capability));
        }

        // Fail loudly at construction (startup) if the capability is not in the authoritative
        // map — a policy that maps to no roles must never silently authorize anyone.
        if (!CapabilityRolePolicy.IsKnown(capability))
        {
            throw new ArgumentException(
                $"Unknown capability '{capability}'. Add it to CapabilityRolePolicy.Map (ADR-005).",
                nameof(capability));
        }

        Capability = capability;
    }

    /// <summary>The capability this requirement demands (e.g. <c>offer.submit</c>).</summary>
    public string Capability { get; }
}
