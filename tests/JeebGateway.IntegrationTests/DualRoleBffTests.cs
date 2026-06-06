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
        var um = new StubUm();
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "kamal-1");
        http.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("userId").GetString().Should().Be("kamal-1");
        doc.RootElement.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "jeeber" });
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
    // F-A — POST /v1/users/me/role/switch (error taxonomy + split signer)
    // -----------------------------------------------------------------

    [Fact]
    public async Task FA_Switch_Unknown_Role_Is_400_InvalidRole_WithoutCallingUM()
    {
        var um = new StubUm();
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "kamal-1");
        http.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "admin" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown role is a gateway-local rejection");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_role");
        um.RoleSwitchCalls.Should().Be(0, "N6 — invalid_role must NOT dial user-management");
    }

    [Fact]
    public async Task FA_Switch_RoleNotAvailable_Maps_To_403_DistinctFrom_400()
    {
        var um = new StubUm
        {
            RoleSwitchThrows = new UserManagementRoleNotAvailableException("kamal-1", Roles.Jeeber)
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "kamal-1");
        http.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden, "UM role_not_available is a 403 signal (N5)");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("role_not_available");
    }

    [Fact]
    public async Task FA_Switch_Returns_UM_Reissued_Token_Verbatim_GatewaySignsNothing()
    {
        const string umToken = "UM.ISSUED.TOKEN";
        var um = new StubUm
        {
            RoleSwitch = new RoleSwitchReissueResult(
                UserId: "kamal-1",
                AccessToken: umToken,
                RefreshToken: "UM.REFRESH",
                ActiveRole: Roles.Jeeber)
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "kamal-1");
        http.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        // CP-C / N11 — the gateway returns the UM-issued token VERBATIM (no re-sign).
        doc.RootElement.GetProperty("accessToken").GetString().Should().Be(umToken,
            "the switch path must relay the UM-issued token unchanged — the gateway signs nothing here");
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("jeeber",
            "opaque 'driver' must translate back to the contract 'jeeber'");
        um.RoleSwitchCalls.Should().Be(1);
        um.LastRoleSwitchOpaqueRole.Should().Be(Roles.Jeeber,
            "the gateway must forward the OPAQUE role to UM, never the Jeeb contract vocab");
    }

    [Fact]
    public async Task FA_Switch_To_Client_Always_Allowed()
    {
        var um = new StubUm
        {
            RoleSwitch = new RoleSwitchReissueResult("kamal-1", "T", "R", Roles.Client)
        };
        using var factory = MakeFactory(new StubOtp(), um, umEnabled: true);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", "kamal-1");
        http.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "client" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "ALT-3 — switch to client is always 200");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("active_role").GetString().Should().Be("client");
    }

    [Fact]
    public async Task FA_Switch_Unauthenticated_Returns_401()
    {
        using var factory = MakeFactory(new StubOtp(), new StubUm(), umEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
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
            new("default-1", false, new[] { Roles.Client }, Roles.Client);
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
    }
}
