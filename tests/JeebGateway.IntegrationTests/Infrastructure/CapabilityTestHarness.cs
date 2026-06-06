using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace JeebGateway.IntegrationTests.Infrastructure;

/// <summary>
/// ADR-005 Layer 2 (capability authorization) — shared test harness for the Verify-phase suite.
///
/// <para>Generalizes the two per-file <c>MintGatewayToken</c> helpers so every capability case is a
/// one-liner. NO new auth scheme, NO mock auth handler — every token is a REAL gateway-session JWT
/// signed with the host's configured <c>Jwt:SigningKey</c> and validated by the real
/// <c>Program</c> pipeline (guardrail: real Program, real tokens).</para>
///
/// <para><b>Opaque vs canonical vocabulary.</b> Pass OPAQUE role strings (<c>customer</c>/<c>driver</c>)
/// to exercise the PRODUCTION path; pass CANONICAL (<c>client</c>/<c>jeeber</c>) to exercise the
/// test/edge path. The handler canonicalizes both to the map key space — the T1a≡T1b gate proves they
/// produce identical outcomes.</para>
/// </summary>
internal static class CapabilityTestHarness
{
    /// <summary>Stable test subject id (same value the ADR-004 fallback tests use for the edge path).</summary>
    internal const string UserId = "11111111-2222-3333-4444-555555555555";

    /// <summary>The mint gate key used by <see cref="WithMintGate"/> and the super-login helpers.</summary>
    internal const string MintKey = "test-only-mint-key-32-bytes-minimum-xxxxx";

    internal const string MintHeader = "X-Service-Auth-Key";

    /// <summary>
    /// Mint a REAL gateway-session bearer (<c>aud=jeeb-clients</c>) carrying the given role strings
    /// VERBATIM as multivalued <c>roles</c> claims. Pass OPAQUE ("customer"/"driver") for the prod
    /// path or CANONICAL ("client"/"jeeber") for the test/edge path. Zero roles → an authenticated
    /// principal with an empty role set (for the SEP-1 "valid L1, empty L2" case).
    /// </summary>
    internal static string MintBearer(WebApplicationFactory<Program> f, params string[] roles)
        => MintWithAudience(f, AudienceOf(f), roles);

    /// <summary>
    /// Mint a bearer with an EXPLICIT audience. Used by the L1-boundary case (wrong audience must be
    /// rejected at Layer 1 → 401, so Layer 2 never runs).
    /// </summary>
    internal static string MintWithAudience(
        WebApplicationFactory<Program> f, string audience, params string[] roles)
    {
        var cfg = f.Services.GetRequiredService<IConfiguration>();
        var signingKey = cfg["Jwt:SigningKey"]!;
        var issuer = cfg["Jwt:Issuer"]!;

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new("sub", UserId),
            new(ClaimTypes.Sid, UserId),
        };
        claims.AddRange(roles.Select(r => new Claim("roles", r))); // multivalued, verbatim

        var jwt = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    internal static string AudienceOf(WebApplicationFactory<Program> f)
        => f.Services.GetRequiredService<IConfiguration>()["Jwt:Audience"]!;

    /// <summary>
    /// REAL super-login: POST /auth/tokens with the privileged <c>X-Service-Auth-Key</c> and the
    /// desired roles, returning the minted access token. This drives the ACTUAL mint path — never a
    /// hand-crafted admin token (matrix §4 SL-MINT honesty rule). A non-200 here is a top-line
    /// BLOCKER (val-1), surfaced via the EnsureSuccessStatusCode throw.
    /// </summary>
    internal static async Task<string> SuperLoginAsync(
        HttpClient client, string serviceAuthKey, params string[] roles)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/auth/tokens")
        {
            Content = JsonContent.Create(new { userId = UserId, roles }),
        };
        req.Headers.Add(MintHeader, serviceAuthKey);

        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode(); // non-200 mint → BLOCKER, not green-by-workaround

        var body = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>
    /// A factory variant with the mint gate ENABLED and a known key, so the SL-4 (no key → 401) /
    /// SL-4b (wrong key → 403) cases are meaningful. Rate limiting is disabled so the suite is
    /// deterministic under parallel runs.
    /// </summary>
    internal static WebApplicationFactory<Program> WithMintGate(string key)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Security:RateLimit:Enabled"] = "false",
                    ["Security:TokenMint:Enabled"] = "true",
                    ["Security:TokenMint:Key"] = key,
                })));

    /// <summary>
    /// Assert the standard L2 403 body shape. Status-only assertions are rejected (matrix §0 rule 2):
    /// the RFC7807 <c>application/problem+json</c> body with the
    /// <c>https://jeeb.dev/errors/forbidden-capability</c> type is the proof that L2 ran and denied.
    /// </summary>
    internal static async Task AssertForbiddenCapabilityBody(HttpResponseMessage resp)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an identified caller whose user type lacks the capability is a Layer-2 403, never a Layer-1 401");
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var p = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        p.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/forbidden-capability");
        p.GetProperty("status").GetInt32().Should().Be(403);
    }

    /// <summary>Bearer-authorized client.</summary>
    internal static HttpClient WithBearer(this HttpClient client, string bearer)
    {
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return client;
    }
}
