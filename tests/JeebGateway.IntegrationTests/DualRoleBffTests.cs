using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S02 Wave-1 (ADR-003) gateway thin-BFF — F-C / F-B / F-A.
///
/// Covers the translation seam (opaque {customer,driver} -> snake_case {client,jeeber}),
/// the bearer-only identity (I4), the split-signer invariant (CP-C / N11 — the gateway
/// relays the UM-issued token verbatim on the switch path), and the error taxonomy
/// (invalid_role 400 gateway-local no-UM-call N6 vs role_not_available 403 UM-signal N5).
///
/// All collaborators that leave the gateway are stubbed; the in-process TokenService and
/// in-memory user store are the real singletons, so the OTP mint produces a genuine JWT.
/// </summary>
public class DualRoleBffTests
{
    // -----------------------------------------------------------------
    // F-C — OTP verify translates opaque -> snake_case (CP-A / CP-B)
    // -----------------------------------------------------------------

    [Fact]
    public async Task FC_Verify_Translates_DualRole_To_SnakeCase_Contract()
    {
        var otp = new StubOtp();
        var um = new StubUm
        {
            FindOrCreate = new PhoneFindOrCreateResult(
                UserId: "kamal-1",
                IsNew: false,
                PhoneHashRef: string.Empty,
                AvailableRoles: new[] { Roles.Client, Roles.Jeeber }, // customer, driver
                ActiveRole: Roles.Client)
        };
        using var factory = MakeFactory(otp, um, umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000002", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("userId").GetString().Should().Be("kamal-1");
        user.GetProperty("active_role").GetString().Should().Be("client",
            "opaque 'customer' MUST translate to the Jeeb contract 'client'");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "jeeber" },
                "opaque {customer,driver} MUST translate to {client,jeeber}");
        um.FindOrCreateCalls.Should().Be(1, "F-C must orchestrate UM phone find-or-create");
    }

    [Fact]
    public async Task FC_Verify_NewIdentity_Defaults_To_Client()
    {
        var otp = new StubOtp();
        var um = new StubUm
        {
            FindOrCreate = new PhoneFindOrCreateResult("new-1", IsNew: true,
                PhoneHashRef: string.Empty,
                AvailableRoles: new[] { Roles.Client }, ActiveRole: Roles.Client)
        };
        using var factory = MakeFactory(otp, um, umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000010", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("active_role").GetString().Should().Be("client");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client" });
    }

    [Fact]
    public async Task FC_Verify_UM_Fault_Falls_Back_And_Still_Mints()
    {
        var otp = new StubOtp();
        var um = new StubUm { FindOrCreateThrows = new UserManagementCallException("phone/find-or-create", 502) };
        using var factory = MakeFactory(otp, um, umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000011", "code": "1234" }"""));

        // Fail-safe: a UM blip must NOT block a successful OTP validate.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("accessToken").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // -----------------------------------------------------------------
    // F-B — GET /v1/users/me (bearer-only; snake_case)
    // -----------------------------------------------------------------

    [Fact]
    public async Task FB_GetMe_Returns_SnakeCase_Roles_From_Bearer()
    {
        // ADR-004: /v1/users/me is now [Authorize]-gated on the gateway session scheme
        // (aud=jeeb-clients). The roles travel in the gateway-minted bearer's per-role
        // claims — exactly the production path the OTP-login mint produces. (The MVP
        // X-User-Id header path is superseded by the one-session-audience model.)
        var um = new StubUm();
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, "kamal-1", Roles.Client, Roles.Jeeber));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userId").GetString().Should().Be("kamal-1");
        doc.RootElement.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "jeeber" });
    }

    /// <summary>
    /// S02 H-A3 regression lock. A single-role (client-only) identity MUST surface exactly
    /// ["client"] from GET /v1/users/me — the gateway NEVER inflates the available_roles set
    /// it received in the bearer's per-role claims. This is the assertion the live S02 H-A3
    /// red exercised: the red was contaminated UM DATA on a reused phone (the identity carried
    /// jeeber from a prior in-scenario KYC upgrade), NOT a gateway role-inflation bug. This
    /// test pins the no-inflation contract so a future regression that ADDS a role is caught.
    /// </summary>
    [Fact]
    public async Task FB_GetMe_SingleRoleClient_Returns_Only_Client()
    {
        var um = new StubUm();
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        // Bearer carries ONLY the client role — exactly the OTP-login mint for a
        // never-KYC'd customer identity (Sami's intended fixture state).
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, "sami-1", Roles.Client));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userId").GetString().Should().Be("sami-1");
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("client");
        doc.RootElement.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client" },
                "a client-only identity MUST surface exactly [client] — the gateway never adds jeeber");
    }

    /// <summary>
    /// S02 H-A2 regression lock (companion to FC_Verify_NewIdentity_Defaults_To_Client).
    /// When UM's phone find-or-create returns a single-role client identity (the intended
    /// state for a never-KYC'd phone like Sami's), OTP verify MUST surface exactly ["client"]
    /// — proving the gateway relays UM's available_roles verbatim and never injects jeeber.
    /// The live H-A2 red was UM holding [client,jeeber] for the reused phone, not the gateway
    /// inflating the set; this test guards against the gateway ever doing the latter.
    /// </summary>
    [Fact]
    public async Task FC_Verify_SingleRoleClient_Returns_Only_Client()
    {
        var otp = new StubOtp();
        var um = new StubUm
        {
            FindOrCreate = new PhoneFindOrCreateResult(
                UserId: "sami-1",
                IsNew: false,
                PhoneHashRef: string.Empty,
                AvailableRoles: new[] { Roles.Client },
                ActiveRole: Roles.Client)
        };
        using var factory = MakeFactory(otp, um, umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000391", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("userId").GetString().Should().Be("sami-1");
        user.GetProperty("active_role").GetString().Should().Be("client");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client" },
                "a client-only UM identity MUST relay as exactly [client] — no gateway-side role inflation");
        um.FindOrCreateCalls.Should().Be(1, "verify must orchestrate UM phone find-or-create");
    }

    [Fact]
    public async Task FB_GetMe_Unauthenticated_Returns_401()
    {
        using var factory = MakeFactory(new StubOtp(), new StubUm(), umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // ADR-004 (upgrade-not-switch): POST /v1/users/me/role/switch is REMOVED.
    // The ceremony tests that asserted it (FA_Switch_*) are deleted — there is
    // no switch endpoint; acting as jeeber is exercising a jeeber-scoped route
    // with the single gateway-minted session token whose available_roles already
    // carries jeeber after real S03 KYC approval. The removed-route behavior is
    // asserted below (404), and the UM-audience 401 invariant lives in
    // UmIssuerTokenTrustTests.
    // -----------------------------------------------------------------

    [Fact]
    public async Task RoleSwitch_Endpoint_Is_Removed_Returns_404()
    {
        using var factory = MakeFactory(new StubOtp(), new StubUm(), umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, "kamal-1", Roles.Client, Roles.Jeeber));

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "ADR-004 removed the role-switch ceremony — the endpoint no longer exists");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient otp, IUserManagementDualRoleClient um, bool umEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(otp);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton(um);
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = umEnabled;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });
            });
        });

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Mints a genuine gateway session bearer (iss=jeeb-gateway / aud=jeeb-clients) signed
    /// with the test host's Jwt:SigningKey, carrying sub=userId + one "roles" claim per role.
    /// This is the ADR-004 one-session-audience token the OTP-login mint produces in production.
    /// </summary>
    private static string MintGatewayBearer(WebApplicationFactory<Program> factory, string userId, params string[] roles)
    {
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var signingKey = config["Jwt:SigningKey"]!;
        var issuer = config["Jwt:Issuer"]!;
        var audience = config["Jwt:Audience"]!;

        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", userId),
            new(System.Security.Claims.ClaimTypes.Sid, userId),
        };
        if (roles.Length > 0)
        {
            claims.Add(new System.Security.Claims.Claim("active_role", roles[0]));
            foreach (var r in roles) claims.Add(new System.Security.Claims.Claim("roles", r));
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StubOtp : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken ct) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken ct) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubUm : IUserManagementDualRoleClient
    {
        public int FindOrCreateCalls { get; private set; }
        public int RoleSwitchCalls { get; private set; }
        public string? LastRoleSwitchOpaqueRole { get; private set; }

        public PhoneFindOrCreateResult FindOrCreate { get; init; } =
            new("default-1", false, string.Empty, new[] { Roles.Client }, Roles.Client);
        public RoleSwitchReissueResult RoleSwitch { get; init; } =
            new("default-1", "access", "refresh", Roles.Client);
        public UserManagementCallException? FindOrCreateThrows { get; init; }
        public Exception? RoleSwitchThrows { get; init; }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
        {
            FindOrCreateCalls++;
            if (FindOrCreateThrows is not null) throw FindOrCreateThrows;
            return Task.FromResult(FindOrCreate);
        }

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            RoleSwitchCalls++;
            LastRoleSwitchOpaqueRole = opaqueRole;
            if (RoleSwitchThrows is not null) throw RoleSwitchThrows;
            return Task.FromResult(RoleSwitch);
        }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, true));
    }
}
