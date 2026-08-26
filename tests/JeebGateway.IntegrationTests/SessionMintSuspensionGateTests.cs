using System.Net;
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
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmLoginRequest = JeebGateway.service.ServiceUserManagement.LoginRequest;
using UmLoginResponse = JeebGateway.service.ServiceUserManagement.LoginResponse;
using WalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;
using GetHolderWallets = JeebGateway.service.ServiceWallet.GetHolderWallets;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// EVERY session-mint path carries the pre-mint suspension gate. <see cref="AuthOtpSuspendedLoginTests"/>
/// pins the OTP leg; this class pins all four doors that issue a gateway session — OTP verify,
/// role/switch, role/unregister and the email/password facade — because gating one leaves the others
/// open to the same account.
///
/// <para><b>D10-R (2026-08-16) — this fixture used to compose a store production does not build.</b>
/// It stood up <c>UpstreamBackedUsersStore</c> over a fake <c>IUserProjectionStore</c> and called that
/// "EXACTLY the production wiring". It is not: <c>UpstreamBackedUsersStore</c> has no DI registration
/// anywhere in <c>Program.cs</c>, <c>IUserProjectionStore</c> has no implementation in the product at
/// all, and <c>Program.cs</c> binds <c>IUsersStore</c> to <c>InMemoryUsersStore</c> unconditionally.
/// So these tests seeded suspension into a composition the gateway never assembles — the same
/// green-by-construction trap D10 found in the OTP suite, one layer out.</para>
///
/// <para>The suspension authority is ban-service: <c>PATCH /admin/users/{id}/suspend</c> ->
/// <c>OwnerComposedAdminUsers.SuspendAsync</c> -> <c>IBanServiceClient.ApplyTerminalBanAsync</c>, and
/// <c>BanServiceUserSuspensionSource</c> reads it back. The fixture now suspends through that same
/// call against a stateful double, so write and read are joined by the product's own wiring.</para>
///
/// <para>P6 and P7 are what stop P1–P5 being vacuous: P7 shows every one of these routes MINTS when
/// nothing is suspended (so the 403s are not unconditional), and P6 shows a suspension written only
/// to the gateway's own users projection changes nothing (so the gate cannot have drifted back onto
/// a store no administrator can write).</para>
/// </summary>
public sealed class SessionMintSuspensionGateTests
{
    private const string AppId = "jeeb-test-app";
    private const string Reason = "Policy violation — case 4471";

    // A UM-canonical id: ban-service rejects a non-UUID with 400, and the unregister
    // wallet guard only engages for a GUID.
    private const string UserId = "11111111-2222-3333-4444-555555555555";

    /// The four routes that issue a gateway session, as the caller drives them.
    public static TheoryData<string> MintPaths => new() { "otp/verify", "role/switch", "role/unregister", "email/login" };

    // -----------------------------------------------------------------
    // P1 — the suspension read FAULTS → FAIL CLOSED.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P1_OtpVerify_SuspensionSourceFaults_FailsClosed_AndMintsNothing()
    {
        var ban = new FakeBanService { ThrowOnStatus = true };
        using var factory = MakeFactory(ban);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000201", "code": "1234" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "BanServiceUserSuspensionSource deliberately does not catch — an unreachable ban-service "
            + "must reach the gate as a fault, because minting while account status is unknowable is "
            + "the measured live defect again");
        raw.ToLowerInvariant().Should().NotContain("accesstoken");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");

        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/identity_unavailable");
    }

    // -----------------------------------------------------------------
    // P2 — suspended in ban-service, in-process store cold → 403.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P2_OtpVerify_SuspendedInBanService_IsRefused_EvenWithColdInMemoryStore()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);
        var http = factory.CreateClient();
        await SuspendThroughAdminPathCallAsync(ban);

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000202", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "after a bounce the gateway's in-process store is empty; ban-service is the only witness");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Be(Reason);
    }

    // -----------------------------------------------------------------
    // P3 — absence is NOT indeterminacy; a new user still signs in.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P3_OtpVerify_UnknownUser_HealthySuspensionRead_Still_Mints()
    {
        using var factory = MakeFactory(new FakeBanService());
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000203", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a first-time login must not be refused — the gate closes on an UNREADABLE status, "
            + "never on a genuinely absent ban row (live ban-service answers 200 + [] here)");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("accessToken").GetString()!.Split('.').Should().HaveCount(3);
    }

    // -----------------------------------------------------------------
    // P4 — role/switch re-mints a session, so it must carry the gate.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P4_RoleSwitch_SuspendedAccount_Is_Refused_And_ReMints_Nothing()
    {
        var ban = new FakeBanService();
        var um = new StubDualRole();
        using var factory = MakeFactory(ban, um);
        var http = Authorized(factory);
        await SuspendThroughAdminPathCallAsync(ban);

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "admin suspend revokes only REFRESH tokens; the live stateless access token stays valid "
            + "for its remaining TTL, and this route mints a FRESH family that is not in the revoked "
            + "set — so suspension is evaded indefinitely unless the re-mint is gated");
        ProblemType(raw).Should().Be("account_suspended",
            "the refusal must be the moderation gate's, not role_not_available — that route emits a "
            + "403 of its own and would otherwise satisfy this assertion for the wrong reason");
        raw.Should().NotContain("\"accessToken\":\"ey");
        um.RoleSwitchCalls.Should().Be(0,
            "the refusal must precede the upstream role mutation, not follow it");
    }

    // -----------------------------------------------------------------
    // P4b — the SECOND re-mint on the same controller (role/unregister).
    // -----------------------------------------------------------------

    [Fact]
    public async Task P4b_RoleUnregister_SuspendedAccount_Is_Refused_And_ReMints_Nothing()
    {
        var ban = new FakeBanService();
        var um = new StubDualRole();
        using var factory = MakeFactory(ban, um);
        var http = Authorized(factory);
        await SuspendThroughAdminPathCallAsync(ban);

        var resp = await http.PostAsync("/v1/users/me/role/unregister", Json("{}"));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role/unregister re-mints the SAME access+refresh pair as role/switch, so gating only "
            + "the switch leaves an equivalent rotation route open to a suspended account");
        ProblemType(raw).Should().Be("account_suspended");
        raw.Should().NotContain("\"accessToken\":\"ey");
        um.RemoveRoleCalls.Should().Be(0,
            "the refusal must precede the upstream role revoke, not follow it");
    }

    // -----------------------------------------------------------------
    // P5 — the email/password facade mints the same session.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P5_EmailLogin_SuspendedAccount_Is_Refused_AndMintsNothing()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);
        var http = factory.CreateClient();
        await SuspendThroughAdminPathCallAsync(ban);

        var resp = await http.PostAsync("/v1/auth/login",
            Json("""{ "email": "banned@example.com", "password": "correct-horse" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the email funnel mints the IDENTICAL gateway session as OTP verify — gating only the "
            + "OTP leg leaves a second, anonymous door into the same account");
        ProblemType(raw).Should().Be("account_suspended");
        raw.ToLowerInvariant().Should().NotContain("accesstoken");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");
    }

    // -----------------------------------------------------------------
    // P6 — FALSIFICATION: the gateway's own users projection does not enforce.
    // -----------------------------------------------------------------

    /// <summary>
    /// Suspension written ONLY to <see cref="IUsersStore"/> — process RAM no administrator can reach,
    /// and the store the gate used to read — must change nothing on any mint path. This goes red the
    /// moment anyone re-points the gate at a store the product does not write, which is exactly how
    /// D10 shipped green.
    /// </summary>
    [Theory]
    [MemberData(nameof(MintPaths))]
    public async Task P6_MintPath_Admits_When_Suspension_Is_Written_Only_To_The_Gateway_Users_Projection(string path)
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban, new StubDualRole());

        var store = factory.Services.GetRequiredService<IUsersStore>();
        var profile = await store.GetOrCreateAsync(UserId, CancellationToken.None);
        var updated = await store.SuspendAsync(profile.Id, Reason, "admin-test", CancellationToken.None);
        updated!.IsSuspended.Should().BeTrue("the fixture's own write must land, or this proves nothing");

        var resp = await CallMintPathAsync(factory, path);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the gateway's in-process users projection is NOT the suspension authority; a suite that "
            + "suspends there and sees a 403 is reading its own write back out of RAM");
    }

    // -----------------------------------------------------------------
    // P7 — CONTROL: with nothing suspended, every one of these routes mints.
    // -----------------------------------------------------------------

    /// <summary>
    /// The opposite-answer control for P1–P5. Without it a gate that refused unconditionally — or a
    /// fixture whose routes 500 on a missing dependency — would satisfy every refusal assertion above.
    /// </summary>
    [Theory]
    [MemberData(nameof(MintPaths))]
    public async Task P7_MintPath_Mints_When_Nothing_Is_Suspended(string path)
    {
        using var factory = MakeFactory(new FakeBanService(), new StubDualRole());

        var resp = await CallMintPathAsync(factory, path);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an unsuspended account must still reach a session through every door");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("accessToken");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// The EXACT call OwnerComposedAdminUsers.SuspendAsync makes, so the fixture cannot drift
    /// from the product's real write.
    private static async Task SuspendThroughAdminPathCallAsync(IBanServiceClient ban)
    {
        var status = await ban.ApplyTerminalBanAsync(UserId, "red", CancellationToken.None);
        status.IsCurrentlyBanned.Should().BeTrue("the fixture must reproduce the measured live state");
    }

    private static Task<HttpResponseMessage> CallMintPathAsync(
        WebApplicationFactory<Program> factory, string path) => path switch
        {
            "otp/verify" => factory.CreateClient().PostAsync("/v1/auth/otp/verify",
                Json("""{ "phone": "+9613000204", "code": "1234" }""")),
            "role/switch" => Authorized(factory).PostAsync("/v1/users/me/role/switch",
                Json("""{ "role": "jeeber" }""")),
            "role/unregister" => Authorized(factory).PostAsync("/v1/users/me/role/unregister", Json("{}")),
            "email/login" => factory.CreateClient().PostAsync("/v1/auth/login",
                Json("""{ "email": "user@example.com", "password": "correct-horse" }""")),
            _ => throw new InvalidOperationException($"unknown mint path '{path}'"),
        };

    /// The RFC 7807 `type` suffix, so a moderation 403 is distinguishable from any other 403.
    private static string ProblemType(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.TryGetProperty("type", out var t)
            ? (t.GetString() ?? string.Empty).Split('/').Last()
            : string.Empty;
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, UserId, Roles.Client, Roles.Jeeber));
        return http;
    }

    /// <summary>
    /// Production wiring as Program.cs actually builds it: IUsersStore stays the InMemoryUsersStore
    /// Program.cs binds, IUserSuspensionSource stays BanServiceUserSuspensionSource, and only the
    /// upstream transports a test must own are doubled.
    /// </summary>
    private static WebApplicationFactory<Program> MakeFactory(
        IBanServiceClient ban, StubDualRole? dualRole = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtp());
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(dualRole ?? new StubDualRole());
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(new StubUmClient(UserId));
                services.RemoveAll<WalletClient>();
                services.AddSingleton<WalletClient>(new StubWalletClient());

                // ban-service is the suspension authority; the double is stateful so a ban written by
                // the admin call is the same row the mint gate reads.
                services.RemoveAll<IBanServiceClient>();
                services.AddSingleton(ban);

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;   // production: UM is the identity authority
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    /// <summary>
    /// Stateful stand-in for ban-service: ApplyTerminalBanAsync records the row the admin suspend
    /// writes and GetStatusAsync returns it, so the test cannot pass by writing and reading two
    /// different places. <see cref="ThrowOnStatus"/> simulates the service being unreachable.
    /// </summary>
    private sealed class FakeBanService : IBanServiceClient
    {
        private readonly Dictionary<string, BanStatusItem> _bans = new();

        public bool ThrowOnStatus { get; init; }

        public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
        {
            if (ThrowOnStatus) throw new HttpRequestException("ban-service unreachable");
            return Task.FromResult(new BanStatusesResult
            {
                UserId = userId,
                BanStatuses = _bans.TryGetValue(userId, out var row)
                    ? new[] { row }
                    : Array.Empty<BanStatusItem>(),
            });
        }

        public Task<BanStatusItem> ApplyTerminalBanAsync(string userId, string policyKey, CancellationToken ct)
        {
            var row = new BanStatusItem
            {
                UserId = userId,
                BanType = policyKey,
                CurrentStage = 3,
                Status = "BAN",
                Message = Reason,
                BannedUntil = null,
                LastUpdated = DateTimeOffset.UtcNow,
                IsCurrentlyBanned = true,
            };
            _bans[userId] = row;
            return Task.FromResult(row);
        }

        public Task<BanStatusItem> ApplyBanAsync(string userId, string banType, CancellationToken ct)
            => ApplyTerminalBanAsync(userId, banType, ct);

        public Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
        {
            _bans.Remove(userId, out var old);
            return Task.FromResult(new BanResetResult { OldStatus = old, NewStatus = null, Updated = old is not null });
        }
    }

    private sealed class StubDualRole : IUserManagementDualRoleClient
    {
        public int RoleSwitchCalls { get; private set; }
        public int RemoveRoleCalls { get; private set; }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(new PhoneFindOrCreateResult(
                UserId, false, new[] { Roles.Client, Roles.Jeeber }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            RoleSwitchCalls++;
            return Task.FromResult(new RoleSwitchReissueResult(userId, "um-access", "um-refresh", opaqueRole));
        }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client, Roles.Jeeber }, true));

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            RemoveRoleCalls++;
            return Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client }, true));
        }

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserRolesResult?>(
                new UserRolesResult(userId, new[] { Roles.Client, Roles.Jeeber }, Roles.Client));
    }

    /// UM accepts the password; the gateway is what must refuse the suspended account.
    private sealed class StubUmClient : UmClient
    {
        private readonly string _userId;

        public StubUmClient(string userId) : base("http://localhost", new HttpClient())
            => _userId = userId;

        public override Task<UmLoginResponse> LoginAsync(UmLoginRequest? body, CancellationToken ct)
            => Task.FromResult(new UmLoginResponse
            {
                UserId = _userId,
                AuthToken = "um-access",
                RefreshToken = "um-refresh",
            });
    }

    /// No wallet provisioned — the 404 the unregister guard reads as an honest zero balance.
    private sealed class StubWalletClient : WalletClient
    {
        public StubWalletClient() : base("http://localhost", new HttpClient()) { }

        public override Task<GetHolderWallets> WalletsAsync(Guid holderId, CancellationToken ct)
            => throw new WalletApiException("not found", 404, "{}", EmptyHeaders, null);
    }

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private sealed class StubOtp : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken ct) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken ct) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private static string MintGatewayBearer(
        WebApplicationFactory<Program> factory, string userId, params string[] roles)
    {
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(config["Jwt:SigningKey"]!)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", userId),
            new(System.Security.Claims.ClaimTypes.Sid, userId),
            new("active_role", roles[0]),
        };
        claims.AddRange(roles.Select(r => new System.Security.Claims.Claim("roles", r)));

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: config["Jwt:Issuer"], audience: config["Jwt:Audience"],
            claims: claims, expires: DateTime.UtcNow.AddMinutes(15), signingCredentials: creds);
        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
