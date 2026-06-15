using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S12.N2 — per-request role-less token mint (PR #193) and the S01 regression
/// guard it is explicitly designed to protect.
///
/// The mint distinguishes an EXPLICIT empty <c>roles:[]</c> (→ role-less token,
/// no <c>roles</c> claim, <c>active_role=""</c>, whose whole purpose is to be
/// rejected 403 by the downstream capability guard) from an ABSENT/null
/// <c>roles</c> field (→ unchanged profile-default fallback — the S01 path).
/// Both the reviews and the owner decision (cto-s12, ADR-005) fix the correct
/// status for a role-less authenticated caller at a capability-guarded route as
/// 403, NOT 400. The original diff asserted this only in prose; these pin it.
/// </summary>
public class RolelessMintTokensTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string MintKey = "test-only-mint-key-32-bytes-minimum-xxxxx";
    private const string MintHeader = "X-Service-Auth-Key";

    private readonly WebApplicationFactory<Program> _factory;

    public RolelessMintTokensTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RateLimit:Enabled"] = "false",
                    ["Security:TokenMint:Enabled"] = "true",
                    ["Security:TokenMint:Key"] = MintKey
                });
            });
        });
    }

    // ----------------------------------------------------------------
    // S12.N2 — explicit roles:[] mints a genuinely role-less token.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Explicit_Empty_Roles_Mints_RoleLess_Token_With_Empty_ActiveRole()
    {
        var resp = await AuthorizedClient().PostAsJsonAsync("/auth/tokens", new
        {
            userId = "u-n2-roleless",
            roles = Array.Empty<string>()   // EXPLICIT roles:[] — the N2 opt-in signal
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = (await resp.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);

        token.Subject.Should().Be("u-n2-roleless");
        token.Claims.Where(c => c.Type == "roles").Should().BeEmpty(
            "an explicit roles:[] mint must produce NO roles claim (role-less token)");
        token.Claims.Single(c => c.Type == "active_role").Value.Should().BeEmpty(
            "the role-less token's active_role must be empty so no capability is granted downstream");
    }

    // ----------------------------------------------------------------
    // S12.N2 — the role-less identity is rejected 403 (capability), NOT 401
    // and NOT 400, at a capability-guarded route. UserIdentity.GetRoles
    // derives the role set identically from a minted token's roles claims and
    // the trusted X-User-Id/X-User-Roles edge headers, so a role-less
    // IDENTIFIED caller (no roles) exercises the exact CapabilityAuthorizationHandler
    // path a role-less token hits — and must 403.
    // ----------------------------------------------------------------
    [Fact]
    public async Task RoleLess_Identity_Is_Rejected_403_At_Capability_Guarded_Route()
    {
        // First prove the mint really yields a role-less token (the producer side).
        var minted = await AuthorizedClient().PostAsJsonAsync("/auth/tokens", new
        {
            userId = "u-n2-403",
            roles = Array.Empty<string>()
        });
        var pair = (await minted.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        token.Claims.Where(c => c.Type == "roles").Should().BeEmpty();

        // Now the consumer side: a role-less but IDENTIFIED caller hitting a
        // capability-guarded route (dispute.read.mine) must be 403 — identified
        // (so not 401) yet holding no capability (so not 200, not 400).
        var roleless = _factory.CreateClient();
        roleless.DefaultRequestHeaders.Add("X-User-Id", "u-n2-403");
        // Deliberately NO X-User-Roles and no role claims → empty role set.

        var resp = await roleless.GetAsync("/v1/disputes");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a role-less authenticated caller's correct status at a capability gate is 403 (capability), not 401 (auth) or 400");
    }

    [Fact]
    public async Task Identified_Caller_With_A_Role_Is_Allowed_At_The_Same_Route()
    {
        // Control: the SAME route admits an identified caller that DOES hold the
        // mapped role — proves the 403 above is the role-less gate, not a blanket deny.
        var withRole = _factory.CreateClient();
        withRole.DefaultRequestHeaders.Add("X-User-Id", "u-n2-ok");
        withRole.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);

        var resp = await withRole.GetAsync("/v1/disputes");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ----------------------------------------------------------------
    // S01 regression guard — ABSENT roles falls back to the profile default,
    // and must NOT be collapsed into the role-less path.
    // ----------------------------------------------------------------
    [Fact]
    public async Task Absent_Roles_Falls_Back_To_Profile_Default_Roles()
    {
        // Seed a profile with a real role; the mint OMITS roles entirely (S01 shape).
        Seed(new UserProfile
        {
            Id = "u-s01",
            Phone = "+96170000001",
            Name = "S01 User",
            Roles = new List<string> { Roles.Client },
            ActiveRole = Roles.Client,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        // No `roles` field at all — the historical fallback path.
        var resp = await AuthorizedClient().PostAsJsonAsync("/auth/tokens", new { userId = "u-s01" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = (await resp.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);

        token.Claims.Where(c => c.Type == "roles").Select(c => c.Value)
            .Should().ContainSingle().Which.Should().Be(Roles.Client,
            "S01: an ABSENT roles field must keep the profile-default fallback — it must NOT become a role-less token");
        token.Claims.Single(c => c.Type == "active_role").Value.Should().NotBeEmpty(
            "absent roles must NOT collapse into the explicit-empty role-less path");
    }

    [Fact]
    public async Task Explicit_NonEmpty_Roles_Are_Embedded_Verbatim()
    {
        // The third arm: an explicit non-empty roles list is honoured as-is
        // (neither role-less nor profile-default), distinct from both N2 and S01.
        var resp = await AuthorizedClient().PostAsJsonAsync("/auth/tokens", new
        {
            userId = "u-explicit-role",
            roles = new[] { Roles.Client }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var pair = (await resp.Content.ReadFromJsonAsync<TokenPairResponse>())!;
        var token = new JwtSecurityTokenHandler().ReadJwtToken(pair.AccessToken);
        token.Claims.Where(c => c.Type == "roles").Select(c => c.Value)
            .Should().ContainSingle().Which.Should().Be(Roles.Client);
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private HttpClient AuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(MintHeader, MintKey);
        return client;
    }

    private void Seed(UserProfile profile)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(profile);
    }
}
