using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;

namespace JeebGateway.Auth.Capabilities;

/// <summary>
/// ADR-005 Layer 2 — the capability authorization handler. Satisfies
/// <see cref="CapabilityRequirement"/> by checking whether the caller's user TYPE (its roles)
/// holds the required capability per the authoritative <see cref="CapabilityRolePolicy"/> map.
///
/// <para><b>Reads roles ONLY, never audience (binding ADR-005 invariant).</b> Audience is
/// Layer 1's concern (<see cref="JeebGateway.Auth.GatewayAudienceRequirement"/> / ADR-004). This
/// handler must never inspect <c>aud</c> — doing so re-introduces the authn/authz conflation the
/// owner flagged. The 401-vs-403 split is the tell: a wrong-audience token is rejected at Layer 1
/// (401) and never reaches here; a valid <c>aud=jeeb-clients</c> caller with the wrong user type
/// reaches here and is denied (403).</para>
///
/// <para><b>Canonicalization (SA load-bearing finding).</b> The PRODUCTION <c>roles</c> claim
/// carries OPAQUE values (<c>customer</c>/<c>driver</c> — see <see cref="Roles"/>); the test/edge
/// vocabulary is canonical (<c>client</c>/<c>jeeber</c>); the map keys on canonical. This handler
/// canonicalizes EVERY principal role via <see cref="JeebRoleTranslator.ToContract"/> immediately
/// before intersecting with the map, so one code path covers both vocabularies with zero map
/// duplication and zero mint/UM change. <c>admin</c> passes through unchanged.</para>
///
/// <para><b>Role-source precedence</b> mirrors <see cref="UserIdentity.GetRoles"/>: the multivalued
/// <c>roles</c> claim first, then the comma-separated <c>X-User-Roles</c> edge header — so the
/// admin/edge <c>X-User-Id</c>+<c>X-User-Roles</c> path is authorized identically to a bearer.</para>
/// </summary>
public sealed class CapabilityAuthorizationHandler : AuthorizationHandler<CapabilityRequirement>
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CapabilityAuthorizationHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CapabilityRequirement requirement)
    {
        var http = _httpContextAccessor.HttpContext;
        if (http is null)
        {
            // No request context → cannot resolve roles → leave unmet (framework returns 403,
            // since Layer 1 already established an authenticated principal to reach here).
            return Task.CompletedTask;
        }

        // The roles the capability holds, in the CANONICAL key space the map uses.
        var allowedCanonical = CapabilityRolePolicy.RolesFor(requirement.Capability);

        // Resolve the caller's roles (claims-then-edge-header precedence), then canonicalize
        // EACH role into the map's key space. This is the prod-opaque → canonical normalization
        // that makes the same comparison correct for both prod (customer/driver) and test/edge
        // (client/jeeber) tokens. NEVER reads audience.
        var principalRoles = UserIdentity.GetRoles(http);

        foreach (var raw in principalRoles)
        {
            var canonical = JeebRoleTranslator.ToContract(raw);
            if (canonical.Length == 0)
            {
                continue;
            }

            foreach (var allowed in allowedCanonical)
            {
                if (string.Equals(canonical, allowed, StringComparison.OrdinalIgnoreCase))
                {
                    context.Succeed(requirement);
                    return Task.CompletedTask;
                }
            }
        }

        // No canonicalized role intersects the capability's role set → leave unmet → 403.
        return Task.CompletedTask;
    }
}
