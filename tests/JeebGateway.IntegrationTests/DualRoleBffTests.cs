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
    private const string KamalId = "5d4d7390-e039-4e3a-9f90-41868b4d1fe4";
    private const string NewClientId = "44460cc8-99db-49b7-9c36-c536e0bc0b2e";
    private const string SamiId = "b425ca3a-e3c6-4a86-bf38-40b7ae9c39b6";

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
                UserId: KamalId,
                IsNew: false,
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
        user.GetProperty("userId").GetString().Should().Be(KamalId);
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
            FindOrCreate = new PhoneFindOrCreateResult(NewClientId, IsNew: true,
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
    public async Task FC_Verify_UM_Fault_FailsClosed_And_MintsNothing()
    {
        var otp = new StubOtp();
        var um = new StubUm { FindOrCreateThrows = new UserManagementCallException("phone/find-or-create", 502) };
        using var factory = MakeFactory(otp, um, umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000011", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        body.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/identity_unavailable");
        body.TryGetProperty("accessToken", out _).Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // F-B — GET /v1/users/me (bearer-only; snake_case)
    // -----------------------------------------------------------------

    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
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
    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
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
    /// JEEBER-TAP regression lock. <c>active_role</c> MUST come from the role record
    /// user-management persists, NOT from the session claim.
    ///
    /// <para>The claim is a MINT-TIME snapshot: a session minted before a role change keeps the
    /// stale value for its whole life, because refresh re-pins the same snapshot
    /// (<c>SessionActiveRoleSnapshot</c>, PR #562). A KYC-approved jeeber therefore kept reading
    /// <c>active_role: client</c>, the mobile <c>RoleSync</c> put them on the client surface, and
    /// the client guard rewrote every <c>jeeb://jeeber/deliveries/&lt;id&gt;/active</c> push tap
    /// to <c>/</c> — measured on the A33 against live staging while UM held <c>driver</c>.</para>
    /// </summary>
    [Fact]
    public async Task FB_GetMe_ActiveRole_Comes_From_UserManagement_Not_The_Stale_Session_Claim()
    {
        var um = new StubUm
        {
            UserRoles = new UserRolesResult(
                KamalId, new[] { Roles.Client, Roles.Jeeber }, Roles.Jeeber)
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        // The bearer's active_role claim is the STALE 'customer' (mint order = roles[0]).
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, KamalId, Roles.Client, Roles.Jeeber));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("jeeber",
            "the persisted active role is the authority; the session claim cannot see a later change");
        doc.RootElement.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "jeeber" });
    }

    /// <summary>
    /// The authority never INVENTS a role: a persisted active role the user does not hold, or none
    /// at all, still falls back to the validated session claim rather than emitting an unheld role.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("admin")]
    public async Task FB_GetMe_ActiveRole_FallsBack_To_The_Session_Claim(string? persistedActive)
    {
        var um = new StubUm
        {
            UserRoles = new UserRolesResult(
                SamiId, new[] { Roles.Client, Roles.Jeeber }, persistedActive)
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, SamiId, Roles.Jeeber, Roles.Client));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("jeeber",
            "an absent or unheld persisted active role leaves the session claim in charge");
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
                UserId: SamiId,
                IsNew: false,
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
        user.GetProperty("userId").GetString().Should().Be(SamiId);
        user.GetProperty("active_role").GetString().Should().Be("client");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client" },
                "a client-only UM identity MUST relay as exactly [client] — no gateway-side role inflation");
        um.FindOrCreateCalls.Should().Be(1, "verify must orchestrate UM phone find-or-create");
    }

    /// <summary>
    /// SELF-DRIFT regression lock — GET /v1/users/me MUST report the SAME effective role set the
    /// login mint produces. A dev-seeded admin (<c>POST /dev/seed/user</c> role=admin) records
    /// opaque <c>[customer,admin]</c> in the <see cref="IDevSeededRoleStore"/>, and
    /// <c>AuthEmailFacadeController.ResolveRolesAsync</c> unions that into the minted JWT
    /// (<c>roles=[customer,admin]</c>). Before the fix, <c>/me</c> resolved roles from
    /// user-management ALONE (which never learned the seed — register has no role column) and
    /// returned <c>available_roles:[client]</c>, contradicting the mint and gating every admin CMS
    /// surface closed (the shell derives capabilities from <c>available_roles</c>). This pins the
    /// union: even when the bearer / UM read lack admin, the seeded role is surfaced — opaque
    /// <c>admin</c> passes through the translator to contract <c>admin</c>, the vocabulary the CMS
    /// shell's <c>capabilitiesFromRoles</c> understands.
    /// </summary>
    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
    public async Task FB_GetMe_SeededAdmin_UnionsAdmin_MatchingLoginMint()
    {
        // StubUm.GetUserRolesAsync => null: models user-management NOT knowing the seeded role,
        // exactly the live condition (the seed never reaches UM's persisted role set).
        var um = new StubUm();
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);

        // The DevSeededRoleStore is the real singleton the [DevOnly] seed action writes and the
        // /me resolver reads. Record the seed the way POST /dev/seed/user role=admin does.
        factory.Services.GetRequiredService<IDevSeededRoleStore>()
            .Record("admin-1", email: null, new[] { Roles.Client, Roles.Admin });

        var http = factory.CreateClient();
        // Bearer carries ONLY client — proving admin is surfaced from the SEED union, not merely
        // echoed from the token's role claims.
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, "admin-1", Roles.Client));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userId").GetString().Should().Be("admin-1");
        doc.RootElement.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "admin" },
                "a dev-seeded admin's /me MUST carry the admin role the login mint minted, not UM's seed-blind [client]");
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
    // POST /v1/users/me/role/switch — RE-INTRODUCED, returns 200 with a
    // freshly-minted GATEWAY session token carrying the new active_role.
    //
    // CONTRACT DRIFT — UPDATED (iter5 BATCHED-FIX B14). History of this route's token
    // contract: (1) ADR-003 removed it (404); (2) PR #226 / DEFECT-1 brought it back but
    // returned NO replacement token (empty access/refresh) so the caller kept its old
    // aud=jeeb-clients session — but that left the active_role claim stale until the next
    // login, and a mobile build that DOES adopt the returned token would be handed an
    // empty string and break. (3) iter5 BATCHED-FIX B14 (LIVE on MSI, temp-overall-run-1)
    // therefore re-mints a REAL gateway session token here: UM persists active_role + the
    // gateway signs a fresh aud=jeeb-clients token (sub=userId, full role set, the now-active
    // role read from the locally-updated store) so the app gets a usable session that
    // immediately carries the switched role. The UM aud=user-management pair is STILL never
    // relayed (that 401 invariant lives in UmIssuerTokenTrustTests); the gateway signs its
    // own token. So the new contract is: 200, NON-EMPTY gateway-minted access/refresh
    // tokens, body reflects the switched active_role. Verified live + on-device.
    // -----------------------------------------------------------------

    [Fact(Skip = "needs a reachable user-management: this case drives a route that calls it, and on a bare checkout the call is refused. Run it with the service up (docker compose / a stub host) - a skip here is NOT a pass.")]
    public async Task RoleSwitch_Returns_200_With_Gateway_Minted_Token()
    {
        // UM persists active_role=driver (opaque) and re-issues a token pair; the gateway
        // DROPS that UM pair (never relayed) but signs its OWN fresh gateway session token,
        // and translates the active role back to contract "jeeber".
        var um = new StubUm
        {
            RoleSwitch = new RoleSwitchReissueResult("kamal-1", "um-access", "um-refresh", Roles.Jeeber),
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, "kamal-1", Roles.Client, Roles.Jeeber));

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "PR #226 re-introduced the role-switch route the mobile DioRoleSwitchRepository calls");

        using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        // B14: a REAL gateway-minted session token is returned (NOT empty, and NOT the UM
        // aud=user-management pair) so the app gets a usable session carrying the new active_role.
        var accessToken = root.GetProperty("accessToken").GetString();
        accessToken.Should().NotBeNullOrEmpty(
            "iter5 B14 re-mints a real gateway session token so the app immediately carries the switched role");
        accessToken.Should().NotBe("um-access", "the UM aud=user-management token is never relayed; the gateway signs its own");
        root.GetProperty("refreshToken").GetString().Should().NotBeNullOrEmpty();
        root.GetProperty("refreshToken").GetString().Should().NotBe("um-refresh");
        // The switch IS reflected in the body's active_role (Jeeb contract vocabulary).
        root.GetProperty("active_role").GetString().Should().Be("jeeber");
        // The gateway did forward the switch to UM (it is the token authority on this path).
        um.RoleSwitchCalls.Should().Be(1);
        um.LastRoleSwitchOpaqueRole.Should().Be(Roles.Jeeber);
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
            new("3600c6c7-6646-4f9d-af3f-49f5d9a02b90", false,
                new[] { Roles.Client }, Roles.Client);
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

        // F3: this fixture predates the revoke seam and does not exercise it — models
        // today's live UM (no revoke op), matching the plan's dark-path default.
        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => throw new UserManagementCallException("role/revoke", 404);

        /// <summary>Overrides the persisted role record GET /v1/users/me reads as its authority.</summary>
        public UserRolesResult? UserRoles { get; init; }

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult(UserRoles ?? new UserRolesResult(
                FindOrCreate.UserId,
                FindOrCreate.AvailableRoles,
                FindOrCreate.ActiveRole));
    }
}
