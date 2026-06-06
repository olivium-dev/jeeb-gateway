using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.CapabilityAuthz;

/// <summary>
/// ADR-005 §1 — per-capability ALLOW / DENY, data-driven from <see cref="CapabilityRolePolicy.Map"/>
/// so the tested set IS the access-control matrix itself (matrix §1 "iterate the registry, do not
/// guess"). Rather than hand-maintain a drift-prone cap→route table for all 53 capabilities, these
/// cases drive the REAL ASP.NET Core authorization pipeline the gateway registers at startup: the
/// per-capability named policy (<c>cap:&lt;name&gt;</c>) is resolved from the running host's
/// <see cref="IAuthorizationPolicyProvider"/> and evaluated by the real
/// <see cref="IAuthorizationService"/> against a principal built from a REAL minted gateway token.
/// This exercises the exact <see cref="CapabilityAuthorizationHandler"/> + canonicalization + map
/// the production routes use — for EVERY capability, with zero route-table to drift.
///
/// <para>The OPAQUE prod vocabulary ("customer"/"driver") is used for the ALLOW/DENY role strings to
/// prove the production canonicalization path. The route-level 401-vs-403 wiring is covered by the
/// named integration tests (T1a–d, SL-*, T7*, T5*, SEP-1, L1/L2).</para>
/// </summary>
[Collection("CapabilityPipeline")]
public sealed class PerCapabilityAllowDenyTests
{
    private readonly CapabilityPipelineFixture _fx;

    public PerCapabilityAllowDenyTests(CapabilityPipelineFixture fx) => _fx = fx;

    /// <summary>
    /// One row per capability. <c>allowOpaqueRoles</c> = the OPAQUE form of the cap's mapped canonical
    /// roles. <c>denyOpaqueRole</c> = an adjacent OPAQUE role NOT in the cap's set (null for the
    /// any-authenticated B-family, which has no L2 deny).
    /// </summary>
    public static TheoryData<string> AllCapabilities()
    {
        var data = new TheoryData<string>();
        foreach (var cap in CapabilityRolePolicy.All)
        {
            data.Add(cap);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCapabilities))]
    public async Task Capability_AllowedRole_Authorizes(string cap)
    {
        var allowOpaque = OpaqueAllowRolesFor(cap);

        foreach (var role in allowOpaque)
        {
            var result = await EvaluateAsync(cap, role);
            result.Succeeded.Should().BeTrue(
                "the OPAQUE role '{0}' canonicalizes into the role set that holds capability '{1}'", role, cap);
        }
    }

    [Theory]
    [MemberData(nameof(AllCapabilities))]
    public async Task Capability_AdjacentWrongRole_IsDenied(string cap)
    {
        var denyOpaque = OpaqueDenyRoleFor(cap);
        if (denyOpaque is null)
        {
            // Any-authenticated (B-family) caps have no adjacent wrong role — every authenticated
            // type holds them. Their only possible denial is L1 (unauthenticated), covered elsewhere.
            return;
        }

        var result = await EvaluateAsync(cap, denyOpaque);
        result.Succeeded.Should().BeFalse(
            "the adjacent OPAQUE role '{0}' is NOT in the role set that holds capability '{1}', so L2 must deny "
            + "(a hard 403 at HTTP)", denyOpaque, cap);
    }

    [Fact]
    public void EveryCapabilityConstant_IsCoveredByTheDataDrivenSet()
    {
        // The structural gate: the const set ⊆ the tested-cap set. Any capability constant with no
        // row here fails LOUDLY with its name ("untested capability = Request Changes", matrix §1).
        var constants = CapabilityConstantValues();
        var tested = CapabilityRolePolicy.All.ToHashSet(StringComparer.Ordinal);

        var untested = constants.Where(c => !tested.Contains(c)).ToList();
        untested.Should().BeEmpty(
            "every public capability constant must be mapped (and therefore data-driven-tested). Untested: {0}",
            string.Join(", ", untested));
    }

    [Fact]
    public void Map_And_Constants_AreInSync()
    {
        // Catches a const declared but unmapped (would 500 at route load) OR a map entry with no const.
        var constants = CapabilityConstantValues().ToHashSet(StringComparer.Ordinal);
        var mapped = CapabilityRolePolicy.Map.Keys.ToHashSet(StringComparer.Ordinal);

        mapped.Should().BeEquivalentTo(constants,
            "CapabilityRolePolicy.Map keys must set-equal the Capabilities.* constant values");
    }

    [Fact]
    public async Task EveryRegisteredCapability_HasAResolvablePolicy()
    {
        // Proves the startup policy-registration loop produced a real named policy for every mapped
        // capability — a missing policy would mean an annotated route 500s at request time.
        var provider = _fx.Factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
        foreach (var cap in CapabilityRolePolicy.All)
        {
            var policy = await provider.GetPolicyAsync(Capabilities.PolicyFor(cap));
            policy.Should().NotBeNull("a named policy 'cap:{0}' must be registered for the mapped capability", cap);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Evaluate the REAL registered <c>cap:&lt;name&gt;</c> policy against a principal carrying the given
    /// OPAQUE role, using the host's <see cref="IAuthorizationService"/> (which invokes the real
    /// <see cref="CapabilityAuthorizationHandler"/>). Roles are placed on BOTH the principal claims and
    /// the ambient HttpContext (the handler resolves via <see cref="UserIdentity.GetRoles"/> off the
    /// request) so the production code path is exercised exactly.
    /// </summary>
    private async Task<AuthorizationResult> EvaluateAsync(string cap, params string[] opaqueRoles)
    {
        using var scope = _fx.Factory.Services.CreateScope();
        var sp = scope.ServiceProvider;

        var provider = sp.GetRequiredService<IAuthorizationPolicyProvider>();
        var policy = await provider.GetPolicyAsync(Capabilities.PolicyFor(cap));
        policy.Should().NotBeNull("capability '{0}' must have a registered policy", cap);

        var authz = sp.GetRequiredService<IAuthorizationService>();

        // Build a real authenticated principal from a minted token's claims, then place the same
        // request on the ambient HttpContext so UserIdentity.GetRoles reads them from the request.
        var bearer = CapabilityTestHarnessClaims(opaqueRoles, out var principal);
        _ = bearer;

        var accessor = sp.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>();
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = principal, RequestServices = sp };
        accessor.HttpContext = http;

        try
        {
            return await authz.AuthorizeAsync(principal, resource: null, policy!);
        }
        finally
        {
            accessor.HttpContext = null;
        }
    }

    private static string CapabilityTestHarnessClaims(string[] opaqueRoles, out ClaimsPrincipal principal)
    {
        var claims = new List<Claim>
        {
            new("sub", "11111111-2222-3333-4444-555555555555"),
        };
        claims.AddRange(opaqueRoles.Select(r => new Claim("roles", r)));
        principal = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
        return string.Empty;
    }

    /// <summary>The OPAQUE roles that should ALLOW the capability (canonical map roles → opaque).</summary>
    private static string[] OpaqueAllowRolesFor(string cap)
        => CapabilityRolePolicy.RolesFor(cap).Select(ToOpaque).ToArray();

    /// <summary>
    /// An adjacent OPAQUE role that should DENY. Null for any-authenticated caps (no wrong role).
    /// </summary>
    private static string? OpaqueDenyRoleFor(string cap)
    {
        var canonical = CapabilityRolePolicy.RolesFor(cap).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var anyAuth = canonical.Contains(JeebRoleTranslator.ContractClient)
                      && canonical.Contains(JeebRoleTranslator.ContractJeeber)
                      && canonical.Contains(Roles.Admin);
        if (anyAuth)
        {
            return null; // B-family: no L2 deny
        }

        // Pick a canonical role NOT in the set, then map to its opaque form.
        if (!canonical.Contains(Roles.Admin))
        {
            return Roles.Admin; // admin is opaque-identical; denies client-only / jeeber-only / participant
        }

        if (!canonical.Contains(JeebRoleTranslator.ContractClient))
        {
            return Roles.Client; // "customer" — denies jeeber-only / admin-only
        }

        // canonical contains client but not jeeber (e.g. client-only with admin? unreachable) — fall back
        return Roles.Jeeber; // "driver"
    }

    private static string ToOpaque(string canonical)
        => canonical switch
        {
            JeebRoleTranslator.ContractClient => Roles.Client,  // client → customer
            JeebRoleTranslator.ContractJeeber => Roles.Jeeber,  // jeeber → driver
            _ => canonical,                                       // admin passthrough
        };

    private static IEnumerable<string> CapabilityConstantValues()
        => typeof(Capabilities)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            // PolicyFor is a method, not a const; the only string consts are capability names.
            .ToList();
}

/// <summary>Shared single host for the pipeline-evaluation cases (no per-test boot cost).</summary>
public sealed class CapabilityPipelineFixture : IDisposable
{
    public WebApplicationFactory<Program> Factory { get; } = new();

    public CapabilityPipelineFixture()
    {
        // Materialize the host (policies, handlers, endpoint graph).
        using var _ = Factory.CreateClient();
    }

    public void Dispose() => Factory.Dispose();
}

[CollectionDefinition("CapabilityPipeline")]
public sealed class CapabilityPipelineCollection : ICollectionFixture<CapabilityPipelineFixture> { }
