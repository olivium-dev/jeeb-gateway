using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests for the ADDITIVE, ENV-GATED Swagger UI / OpenAPI surface under
/// <c>/swagger*</c> (Program.cs), per the C15 "Staging Swagger for admins"
/// scenario and JEB security guardrails.
///
/// The live gateway runs <c>ASPNETCORE_ENVIRONMENT=Production</c>, so these
/// tests pin the host environment to <c>Production</c> (NOT Development/Testing,
/// which serve Swagger open). Two contracts are asserted under Production:
///
///   * <b>flag OFF (committed default) → 404</b> on every <c>/swagger*</c> path.
///     The surface is indistinguishable from "no such endpoint", so it cannot
///     leak the gateway's route surface on the PUBLIC jeeb.fds-1.com host.
///   * <b>flag ON</b> (<c>Features:Swagger:Enabled=true</c>, the env-add the
///     deploy <c>swagger_ui</c> input applies) → the surface is ADMIN-ROLE-GATED:
///       - admin principal (JWT roles:[admin] OR edge X-User-Roles:admin) → 200
///       - non-admin / anonymous → 404
///     It is NEVER served open under Production.
///
/// This is the honest A4 gate: flag-on without an admin role still yields 404,
/// so a PASS means an admin genuinely gets 200 while a non-admin genuinely does
/// not — the admin gate runs under Production (re-keyed off the dead Staging
/// branch onto the Features:Swagger:Enabled flag).
/// </summary>
public class SwaggerGatingTests
{
    // Mirrors JwtOptions defaults (src/JeebGateway/Tokens/JwtOptions.cs) — the
    // JWT bearer handler validates against the "Jwt" config section.
    private const string SigningKey = "dev-only-signing-key-32-bytes-minimum!!";
    private const string Issuer = "jeeb-gateway";
    private const string Audience = "jeeb-clients";

    // -----------------------------------------------------------------
    // flag OFF (committed default) -> 404 on every /swagger* path,
    // even for an admin — the surface does not exist while off.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Swagger_FlagOff_Production_Returns404_EvenForAdmin(string path)
    {
        using var factory = NewFactory(enabled: false);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintAdminToken());

        var resp = await client.GetAsync(path);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "while Features:Swagger:Enabled is false, /swagger* must behave as if it does not exist");
    }

    // -----------------------------------------------------------------
    // flag ON -> admin (JWT roles:[admin]) gets 200.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Swagger_FlagOn_Production_AdminJwt_Returns200()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintAdminToken());

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an admin-role principal must reach the Swagger surface when the flag is on");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"openapi\"", "the admin gets the real OpenAPI document");
    }

    // -----------------------------------------------------------------
    // flag ON -> admin via edge X-User-Roles header also gets 200
    // (the gateway's dual MVP identity model).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Swagger_FlagOn_Production_AdminEdgeHeader_Returns200()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "admin-edge-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the edge-injected X-User-Roles:admin path must also satisfy the admin gate");
    }

    // -----------------------------------------------------------------
    // flag ON -> NON-admin (JWT roles:[customer]) gets 404.
    // This is what keeps A4 honest: enabling the flag does NOT open the
    // surface to everyone.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Swagger_FlagOn_Production_NonAdminJwt_Returns404()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken("customer"));

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a non-admin principal must be rejected with 404 even when the flag is on");
    }

    [Fact]
    public async Task Swagger_FlagOn_Production_NonAdminEdgeHeader_Returns404()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "cust-edge-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a non-admin edge principal must be rejected with 404 even when the flag is on");
    }

    // -----------------------------------------------------------------
    // flag ON -> ANONYMOUS gets 404 (no leak to unauthenticated callers).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Swagger_FlagOn_Production_Anonymous_Returns404()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/swagger/v1/swagger.json");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "an anonymous request must never reach the Swagger surface on the public Production host");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(bool enabled)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // Pin to Production so the admin-gated flag branch (NOT the open
                // Development/Testing branch) is exercised — this is the live
                // host's environment.
                builder.UseEnvironment("Production");
                builder.UseSetting("Features:Swagger:Enabled", enabled ? "true" : "false");

                // Under Production the BffStartupValidator (AC1) would fail boot
                // when required downstream BaseUrls are absent. This Swagger test
                // does not exercise any downstream, so disable that gate to keep
                // the test isolated to the /swagger* surface.
                builder.UseSetting("BffServices:RequiredInProduction", "false");

                // Ensure the JWT bearer handler validates against the same key /
                // issuer / audience the test mints with (JwtOptions defaults).
                builder.UseSetting("Jwt:SigningKey", SigningKey);
                builder.UseSetting("Jwt:Issuer", Issuer);
                builder.UseSetting("Jwt:Audience", Audience);
            });
    }

    private static string MintAdminToken() => MintToken("admin");

    private static string MintToken(params string[] roles)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim> { new("sub", "swagger-test-user") };
        claims.AddRange(roles.Select(r => new Claim("roles", r)));

        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
