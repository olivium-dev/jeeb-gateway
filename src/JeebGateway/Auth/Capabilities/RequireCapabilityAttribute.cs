using Microsoft.AspNetCore.Authorization;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the single route-declaration attribute. A thin subclass of
/// <see cref="AuthorizeAttribute"/> that points at the per-capability policy
/// (<c>cap:&lt;capability&gt;</c>). Because it derives from <see cref="AuthorizeAttribute"/> it:
/// <list type="bullet">
///   <item>composes with the ASP.NET Core authorization middleware (no bespoke filter),</item>
///   <item>returns the framework's standard 401 (Layer 1) / 403 (Layer 2), and</item>
///   <item>is discoverable by Swagger and <c>IAuthorizationPolicyProvider</c>.</item>
/// </list>
///
/// <para>This REPLACES the legacy <c>RequireRoleAttribute</c> (<see cref="Users.RequireRoleAttribute"/>),
/// an <see cref="Microsoft.AspNetCore.Mvc.Filters.IActionFilter"/> that ran OUTSIDE the auth
/// pipeline and hand-built its 403. Usage: <c>[RequireCapability("offer.submit")]</c> at class or
/// method level. Class-level applies to all actions; method-level overrides for that action.</para>
///
/// <para><b>STATE guardrail.</b> On an action the ADR marks <c>(STATE)</c>, this attribute carries
/// ONLY the coarse capability — ownership / party / SM-legality / BR rules stay in the owning
/// service and must never be expressed as an L2 policy.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequireCapabilityAttribute : AuthorizeAttribute
{
    public RequireCapabilityAttribute(string capability)
        : base(Capabilities.PolicyFor(RequireKnown(capability)))
    {
        Capability = capability;
    }

    /// <summary>The capability this route requires (e.g. <c>offer.submit</c>).</summary>
    public string Capability { get; }

    private static string RequireKnown(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability))
        {
            throw new ArgumentException("Capability must be a non-empty name.", nameof(capability));
        }

        // Fail fast at type-load/attribute-construction if a route names a capability that is not
        // in the authoritative map — there would be no registered policy for it and the request
        // would 500 ("policy not found"), which is worse than a loud build-time/startup failure.
        if (!CapabilityRolePolicy.IsKnown(capability))
        {
            throw new ArgumentException(
                $"Unknown capability '{capability}'. Add it to CapabilityRolePolicy.Map (ADR-005) " +
                "before referencing it from [RequireCapability].",
                nameof(capability));
        }

        return capability;
    }
}
