using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-004 Directive 1 — the gateway-audience auth approach is applied UNIFORMLY to every
/// route via the <c>FallbackPolicy</c>: an endpoint that carries NO authorization metadata
/// (no <c>[Authorize]</c>, no <c>[AllowAnonymous]</c>) is no longer silently anonymous.
///
/// <c>GET /form-builder/languages</c> is the canary: before D1 it had zero authorization
/// metadata and answered anonymously. After D1 the FallbackPolicy governs it, requiring an
/// identified caller — either a validated gateway-session bearer OR the trusted edge
/// <c>X-User-Id</c> header (the admin/edge path the directive requires us to preserve).
///
/// Invariants:
/// <list type="bullet">
///   <item><description>No credential → <b>401</b> (previously 200/503 anonymously).</description></item>
///   <item><description>Trusted edge <c>X-User-Id</c> header → <b>NOT 401</b> (edge path preserved).</description></item>
///   <item><description>Gateway-issued bearer (aud=jeeb-clients) → <b>NOT 401</b>.</description></item>
///   <item><description>An <c>[AllowAnonymous]</c> route (<c>/health</c>) → <b>200</b> with no credential
///     (the public-by-design opt-out still works).</description></item>
/// </list>
/// "NOT 401" (rather than a fixed 200) is asserted because the action may legitimately return
/// 503 when the FormBuilder upstream flag is off — that is a post-authorization outcome and
/// proves the auth gate let the request THROUGH, which is the only thing this test governs.
/// </summary>
public class FallbackPolicyUniformAuthTests
{
    private const string CanaryRoute = "/form-builder/languages";
    private const string EdgeUserId = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task PreviouslyAnonymousRoute_NoCredential_Is_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync(CanaryRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "ADR-004 D1: a route with no authorization metadata is now governed by the FallbackPolicy "
            + "and must reject an unidentified caller (401) — no controller is silently anonymous");
    }

    [Fact]
    public async Task PreviouslyAnonymousRoute_WithEdgeXUserId_Is_Not_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", EdgeUserId);

        var resp = await client.GetAsync(CanaryRoute);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "ADR-004 D1: the trusted edge X-User-Id path must be preserved — the FallbackPolicy "
            + "accepts an X-User-Id-identified caller exactly as the rest of the gateway does");
    }

    [Fact]
    public async Task PreviouslyAnonymousRoute_WithGatewayBearer_Is_Not_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintGatewayToken(factory));

        var resp = await client.GetAsync(CanaryRoute);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "ADR-004 D1: a valid gateway-issued session bearer (aud=jeeb-clients) satisfies the FallbackPolicy");
    }

    [Theory]
    // The public auth ENTRY points must stay anonymous under the FallbackPolicy: a caller
    // logs in / registers WITHOUT a session token. (A 4xx other than 401 — e.g. 400 on an
    // empty/invalid body, or a non-401 upstream code — proves the auth gate let it THROUGH;
    // the only forbidden outcome is 401, which would mean the route was wrongly gated.)
    [InlineData("/api/User/login")]
    [InlineData("/api/User/register")]
    [InlineData("/api/User/social")]
    [InlineData("/api/User/token-login")]
    [InlineData("/v1/auth/otp/request")]
    public async Task PublicAuthEntryPoint_NoCredential_Is_Not_401(string route)
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.PostAsync(route,
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            $"ADR-004 D1: the public auth entry point {route} must remain anonymous ([AllowAnonymous]) — "
            + "a user authenticates THROUGH it without already holding a session token");
    }

    [Fact]
    public async Task AllowAnonymousRoute_NoCredential_Is_200()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "ADR-004 D1: the public-by-design [AllowAnonymous] opt-out (here the /health liveness probe) "
            + "must still answer without a credential");
    }

    private static string MintGatewayToken(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"]!;
        var audience = config["Jwt:Audience"]!;
        var signingKey = config["Jwt:SigningKey"]!;

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[]
            {
                new Claim("sub", EdgeUserId),
                new Claim(ClaimTypes.Sid, EdgeUserId),
                new Claim("roles", "client"),
            },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
