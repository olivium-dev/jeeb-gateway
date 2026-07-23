using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-005 Layer 2 — unit tests for <see cref="CapabilityAuthorizationHandler"/>: the load-bearing
/// canonicalization seam. These prove the handler reads roles (not audience), canonicalizes opaque
/// prod vocabulary into the canonical map key space, honours the claims→edge-header precedence, and
/// fails loudly on an unknown capability. The integration suite (WebApplicationFactory) covers the
/// 401-vs-403 wiring; these isolate the decision logic.
/// </summary>
public sealed class CapabilityAuthorizationHandlerTests
{
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "JeebGateway.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static AuthorizationHandlerContext Evaluate(
        string capability,
        IEnumerable<Claim>? roleClaims = null,
        string? xUserRolesHeader = null)
    {
        var http = new DefaultHttpContext();
        // SEC-C1: the X-User-Roles edge-header path is trusted only from Development/Testing or a
        // secret-gated trusted edge. This unit context simulates the local/dev host so the edge
        // header path (which a real request always evaluates with a populated IHostEnvironment)
        // is honoured exactly as before the hardening.
        http.RequestServices = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment())
            .BuildServiceProvider();

        var claims = new List<Claim>();
        if (roleClaims is not null)
        {
            claims.AddRange(roleClaims);
        }
        // The handler only requires an HttpContext + roles; an (unauthenticated) identity is fine
        // because Layer 1 (audience) is asserted separately by the policy, not by this handler.
        http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: claims.Count > 0 ? "Test" : null));

        if (xUserRolesHeader is not null)
        {
            http.Request.Headers["X-User-Roles"] = xUserRolesHeader;
        }

        var accessor = new HttpContextAccessor { HttpContext = http };
        IAuthorizationHandler handler = new CapabilityAuthorizationHandler(accessor);
        var requirement = new CapabilityRequirement(capability);

        var context = new AuthorizationHandlerContext(
            new[] { requirement },
            http.User,
            resource: null);

        handler.HandleAsync(context).GetAwaiter().GetResult();
        return context;
    }

    [Fact]
    public void Jeeber_canonical_role_satisfies_jeeber_capability()
    {
        var ctx = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "jeeber") });
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Jeeber_OPAQUE_role_driver_satisfies_jeeber_capability_via_canonicalization()
    {
        // Production token carries the opaque "driver"; the handler must canonicalize to "jeeber"
        // before lookup. This is the SA load-bearing finding — prod vocabulary must pass identically.
        var ctx = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "driver") });
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Client_canonical_does_NOT_satisfy_jeeber_capability()
    {
        var ctx = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "client") });
        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void Client_OPAQUE_customer_does_NOT_satisfy_jeeber_capability_canonicalization_covers_prod_and_test()
    {
        // T1 (unit half): minted as opaque "customer" → canonicalized "client" → still denied on a
        // jeeber cap. Proves canonicalization yields the SAME deny for prod-opaque and test-canonical.
        var opaque = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "customer") });
        var canonical = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "client") });

        opaque.HasSucceeded.Should().BeFalse();
        canonical.HasSucceeded.Should().BeFalse();
        opaque.HasSucceeded.Should().Be(canonical.HasSucceeded);
    }

    [Fact]
    public void Admin_role_passes_through_unchanged_and_satisfies_admin_capability()
    {
        var ctx = Evaluate(Capabilities.KycReview, new[] { new Claim("roles", "admin") });
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Admin_is_not_a_superuser_admin_does_NOT_satisfy_jeeber_capability()
    {
        // AC-SL6 (unit half): an admin token gets denied on a jeeber-only capability.
        var ctx = Evaluate(Capabilities.OfferSubmit, new[] { new Claim("roles", "admin") });
        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void Dual_role_opaque_token_satisfies_BOTH_client_and_jeeber_capabilities_no_switch()
    {
        // PO G2 (unit half): a single [customer, driver] token authorizes a client cap AND a jeeber cap.
        var roles = new[] { new Claim("roles", "customer"), new Claim("roles", "driver") };

        Evaluate(Capabilities.RequestCreate, roles).HasSucceeded.Should().BeTrue();   // client cap
        Evaluate(Capabilities.OfferSubmit, roles).HasSucceeded.Should().BeTrue();     // jeeber cap
    }

    [Fact]
    public void Edge_X_User_Roles_header_path_is_honoured_when_no_role_claims()
    {
        // T5 (unit half): the trusted-edge header path drives capabilities when there is no bearer.
        var ctx = Evaluate(Capabilities.KycReview, roleClaims: null, xUserRolesHeader: "admin");
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void Edge_header_opaque_role_is_also_canonicalized()
    {
        var ctx = Evaluate(Capabilities.OfferSubmit, roleClaims: null, xUserRolesHeader: "driver");
        ctx.HasSucceeded.Should().BeTrue();
    }

    [Fact]
    public void No_roles_does_not_satisfy_any_capability()
    {
        var ctx = Evaluate(Capabilities.ProfileReadSelf);
        ctx.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public void Unknown_capability_throws_at_requirement_construction()
    {
        var act = () => new CapabilityRequirement("totally.unknown.capability");
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown capability*");
    }

    [Fact]
    public void RequireCapability_attribute_rejects_unknown_capability()
    {
        var act = () => new RequireCapabilityAttribute("totally.unknown.capability");
        act.Should().Throw<ArgumentException>().WithMessage("*Unknown capability*");
    }

    [Fact]
    public void RequireCapability_attribute_points_at_the_per_capability_policy()
    {
        var attr = new RequireCapabilityAttribute(Capabilities.OfferSubmit);
        attr.Policy.Should().Be(Capabilities.PolicyFor(Capabilities.OfferSubmit));
        attr.Capability.Should().Be(Capabilities.OfferSubmit);
    }

    [Fact]
    public void Every_mapped_capability_resolves_to_a_nonempty_canonical_role_set()
    {
        foreach (var cap in CapabilityRolePolicy.All)
        {
            var roles = CapabilityRolePolicy.RolesFor(cap);
            roles.Should().NotBeEmpty($"capability '{cap}' must map to at least one role");
            roles.Should().OnlyContain(
                r => r == "client" || r == "jeeber" || r == "admin" || r == "partner",
                $"capability '{cap}' must key on canonical roles only");
        }
    }
}
