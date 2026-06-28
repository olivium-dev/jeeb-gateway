using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmServiceClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmUserProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// driver→jeeber projection regression. A real user who already holds the OPAQUE
/// <c>driver</c> role in user-management (e.g. a Verified jeeber) MUST be projected as
/// <c>available_roles=[client,jeeber]</c> / <c>active_role=jeeber</c> after an OTP login —
/// so RoleSync routes the device to the Jeeber shell.
///
/// Root cause this guards: the shared UM phone-identity endpoint is identity-only
/// (JEB-1480) and the role-switch reissue ceremony was removed (ADR-004), so the OTP-login
/// mint defaulted to a CUSTOMER-ONLY session and dropped the user's elevated role. The fix
/// reads the user's authoritative role set from the UM profile at login and bakes it into
/// the session bearer + local projection (the bearer-carries-full-role-set contract that
/// <c>UsersMeController</c> relays verbatim).
/// </summary>
public class DriverJeeberProjectionTests
{
    [Fact]
    public async Task OtpLogin_ForDriverUser_ProjectsClientAndJeeber_FromUmProfile()
    {
        // UM phone find-or-create is identity-only and returns the customer default (JEB-1480).
        var um = new StubUm
        {
            FindOrCreate = new PhoneFindOrCreateResult(
                UserId: "kamal-uuid", IsNew: false,
                AvailableRoles: new[] { Roles.Client }, ActiveRole: Roles.Client),
        };
        // The UM profile (the role authority) holds the elevated driver role.
        var profile = new StubUmProfile(new UmUserProfileResponse
        {
            UserId = "kamal-uuid",
            Username = "jeeb-kamal-seed",
            Available_roles = new List<string> { Roles.Client, Roles.Jeeber }, // customer, driver
            Active_role = Roles.Jeeber,                                          // driver
        });

        using var factory = MakeFactory(um, profile);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000002", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("userId").GetString().Should().Be("kamal-uuid");
        user.GetProperty("active_role").GetString().Should().Be("jeeber");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client", "jeeber" },
                "the gateway must read the user's authoritative roles from the UM profile and translate driver→jeeber");
    }

    [Fact]
    public async Task OtpLogin_WhenUmProfileFaults_FallsBackToFindOrCreateDefault_StaysHttp200()
    {
        // find-or-create default = customer-only; the profile read FAULTS.
        var um = new StubUm
        {
            FindOrCreate = new PhoneFindOrCreateResult(
                UserId: "sami-uuid", IsNew: false,
                AvailableRoles: new[] { Roles.Client }, ActiveRole: Roles.Client),
        };
        var profile = new StubUmProfile(profile: null, throws: true);

        using var factory = MakeFactory(um, profile);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000391", "code": "1234" }"""));

        // Degrade-don't-fail: a profile-enrichment blip must NEVER fail a valid OTP login.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("available_roles").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo(new[] { "client" },
                "a UM profile blip degrades to the find-or-create default — no inflation, no 5xx");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> MakeFactory(
        IUserManagementDualRoleClient um, UmServiceClient umProfile) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtp());
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton(um);
                services.RemoveAll<UmServiceClient>();
                services.AddSingleton(umProfile);
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });
            });
        });

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    /// <summary>UM profile stub — overrides only the role-bearing <c>ProfileAsync</c> read.</summary>
    private sealed class StubUmProfile : UmServiceClient
    {
        private readonly UmUserProfileResponse? _profile;
        private readonly bool _throws;

        public StubUmProfile(UmUserProfileResponse? profile, bool throws = false)
            : base("http://localhost", new HttpClient())
        {
            _profile = profile;
            _throws = throws;
        }

        public override Task<UmUserProfileResponse> ProfileAsync(string userId, CancellationToken cancellationToken)
        {
            if (_throws) throw new Exception("user-management profile unavailable");
            return Task.FromResult(_profile!);
        }
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
        public PhoneFindOrCreateResult FindOrCreate { get; init; } =
            new("default-1", false, new[] { Roles.Client }, Roles.Client);

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(FindOrCreate);

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleSwitchReissueResult(userId, "access", "refresh", opaqueRole));

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { opaqueRole }, true));
    }
}
