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
using FeedbackClient = JeebGateway.service.ServiceFeedback.ServiceFeedbackClient;
using RateeReviewsResponse = JeebGateway.service.ServiceFeedback.RateeReviewsResponse;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;
using WalletClient = JeebGateway.service.ServiceWallet.ServiceWalletClient;
using GetHolderWallets = JeebGateway.service.ServiceWallet.GetHolderWallets;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// OA-34 / owner decision O3 — the DEPLOYED back-office bundle's suspend button.
///
/// <para><b>The call being matched, read out of the shipped artifact, not out of a doc.</b>
/// <c>mf/users/606.js</c> in <c>/opt/jeeb-cms/releases/20260816T081610Z-cms-fe21d9c</c> contains
/// <c>updateAdminUserStatus(e,r,t){let s=this.baseUrl+"/user-management/admin/users/{userId}/status"
/// ...method:"PATCH"...}</c> with body <c>{action:"suspend"|"reinstate", reason?}</c>. The
/// back-office vhost strips one <c>/gateway/</c>, so the gateway must serve
/// <c>PATCH user-management/admin/users/{id}/status</c>. It served nothing there.</para>
///
/// <para><b>The constraint that decides the design (OA-36).</b> <c>POST /v1/auth/refresh</c> is a
/// fifth session-minting door and carries NO suspension gate. A suspension is survivable only
/// because <see cref="JeebGateway.Controllers.AdminUsersController"/> revokes the refresh-token
/// family on the SAME request as the ban write. A ban written straight at ban-service revokes
/// nothing and the account rotates refresh tokens forever. So this route delegates to that
/// controller's own actions rather than re-implementing the ban write — and
/// <see cref="R6_Ban_Written_Directly_To_BanService_Does_NOT_Revoke_The_Refresh_Family"/> is the
/// control that proves the difference is real and that R2 is not vacuous.</para>
///
/// <para><b>Wiring.</b> Production composition as <c>Program.cs</c> builds it —
/// <c>IAdminUserProjection</c> stays <c>OwnerComposedAdminUsers</c>, <c>IUserSuspensionSource</c>
/// stays <c>BanServiceUserSuspensionSource</c>, <c>ITokenService</c> is the real one. Only upstream
/// transports are doubled, and the ban double is STATEFUL so the admin write and the login-gate
/// read are joined by the product's own wiring instead of by the fixture.</para>
/// </summary>
public sealed class CmsAdminUserStatusRouteTests
{
    private const string AppId = "jeeb-test-app";
    private const string Reason = "Policy violation — case 4471";

    /// A UM-canonical id: ban-service rejects a non-UUID with 400.
    private const string UserId = "11111111-2222-3333-4444-555555555555";

    /// The literal the deployed CMS bundle emits, after the vhost strips one /gateway/.
    private const string CmsRoute = "/user-management/admin/users/" + UserId + "/status";

    // -----------------------------------------------------------------
    // R1 — the route the deployed bundle calls EXISTS and suspends.
    // -----------------------------------------------------------------

    [Fact]
    public async Task R1_CmsStatusRoute_Suspend_Returns200_AndFlipsTheRowTheCmsRenders()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);

        var resp = await AdminClient(factory).PatchAsync(CmsRoute,
            Json($$"""{ "action": "suspend", "reason": "{{Reason}}" }"""));

        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the deployed CMS bundle PATCHes exactly this path; before this route existed it 404'd");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        root.GetProperty("userId").GetString().Should().Be(UserId);
        root.GetProperty("isSuspended").GetBoolean().Should().BeTrue();
        // The shipped panel derives its state from `activeRole === "suspended"` (51cf7c9) and the
        // newer bundle reads `isSuspended`; the row must satisfy both or the button will not flip.
        root.GetProperty("activeRole").GetString().Should().Be("suspended");
        root.GetProperty("suspensionReason").GetString().Should().Be(Reason);

        // A REAL suspension: it landed in ban-service, the product's suspension authority.
        ban.IsBanned(UserId).Should().BeTrue("the write must reach ban-service, not a gateway dictionary");
    }

    // -----------------------------------------------------------------
    // R2 — THE INVARIANT: suspending kills the refresh family.
    // -----------------------------------------------------------------

    /// <summary>
    /// Drives real product routes end to end: mint a session on <c>/v1/auth/otp/verify</c>, prove
    /// the refresh token rotates, suspend through the CMS route, then prove the SAME family is dead
    /// on <c>/v1/auth/refresh</c> — the one mint door with no suspension gate.
    /// </summary>
    [Fact]
    public async Task R2_Suspending_Through_The_CmsRoute_Revokes_The_Users_Refresh_Tokens()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);

        var refresh = await MintRefreshTokenAsync(factory);

        // Control leg: the family is live BEFORE the suspension, so the 401 below cannot be a
        // token that was never valid.
        var (before, rotated) = await RotateAsync(factory, refresh);
        before.Should().Be(HttpStatusCode.OK, "the refresh family must be live before the suspension");
        rotated.Should().NotBeNullOrEmpty();

        var suspend = await AdminClient(factory).PatchAsync(CmsRoute,
            Json($$"""{ "action": "suspend", "reason": "{{Reason}}" }"""));
        suspend.StatusCode.Should().Be(HttpStatusCode.OK);

        JsonDocument.Parse(await suspend.Content.ReadAsStringAsync())
            .RootElement.GetProperty("revokedTokenCount").GetInt32()
            .Should().BeGreaterThan(0, "the suspension must sweep the live refresh tokens, not report zero");

        var (after, _) = await RotateAsync(factory, rotated!);
        after.Should().Be(HttpStatusCode.Unauthorized,
            "/v1/auth/refresh has NO suspension gate (OA-36); the ONLY thing standing between a "
            + "suspended account and an indefinite session is this revocation happening on the same "
            + "request as the ban write");
    }

    // -----------------------------------------------------------------
    // R3 — the suspension is visible to the login gate.
    // -----------------------------------------------------------------

    [Fact]
    public async Task R3_Suspension_Written_By_The_CmsRoute_Is_Seen_By_The_Login_Gate()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);

        var suspend = await AdminClient(factory).PatchAsync(CmsRoute,
            Json($$"""{ "action": "suspend", "reason": "{{Reason}}" }"""));
        suspend.StatusCode.Should().Be(HttpStatusCode.OK);

        var login = await factory.CreateClient().PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000301", "code": "1234" }"""));

        login.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the gate reads IUserSuspensionSource -> ban-service, which is what this route writes");

        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Be(Reason);
    }

    // -----------------------------------------------------------------
    // R4 — reinstate: the CMS's second verb on the same route.
    // -----------------------------------------------------------------

    [Fact]
    public async Task R4_CmsStatusRoute_Reinstate_Lifts_The_Suspension_And_Login_Mints_Again()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);
        var admin = AdminClient(factory);

        (await admin.PatchAsync(CmsRoute, Json($$"""{ "action": "suspend", "reason": "{{Reason}}" }""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await admin.PatchAsync(CmsRoute, Json("""{ "action": "reinstate" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("isSuspended").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("activeRole").GetString().Should().NotBe("suspended");
        ban.IsBanned(UserId).Should().BeFalse("reinstate must clear the ban-service row");

        var login = await factory.CreateClient().PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000302", "code": "1234" }"""));
        login.StatusCode.Should().Be(HttpStatusCode.OK, "a reinstated account must sign in again");
    }

    // -----------------------------------------------------------------
    // R5 — CONTROL: nothing suspended, the same probes answer the other way.
    // -----------------------------------------------------------------

    /// <summary>
    /// The opposite-answer control for R2 and R3. Without it, a gate that refused unconditionally —
    /// or a fixture whose routes fault on a missing dependency — would satisfy every refusal above.
    /// </summary>
    [Fact]
    public async Task R5_Control_With_Nothing_Suspended_Login_Mints_And_Refresh_Rotates()
    {
        using var factory = MakeFactory(new FakeBanService());

        var login = await factory.CreateClient().PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000303", "code": "1234" }"""));
        login.StatusCode.Should().Be(HttpStatusCode.OK);

        var refresh = await MintRefreshTokenAsync(factory);
        var (status, _) = await RotateAsync(factory, refresh);
        status.Should().Be(HttpStatusCode.OK,
            "with no suspension the refresh family must rotate — otherwise R2's 401 proves nothing");
    }

    // -----------------------------------------------------------------
    // R6 — CONTROL + OA-36: a ban written DIRECTLY revokes nothing.
    // -----------------------------------------------------------------

    /// <summary>
    /// The negative control that makes R2 non-vacuous, and the standing measurement of OA-36's
    /// residual hole. ban-service is reachable without the gateway; a ban applied there refuses the
    /// account at every gated door yet leaves the refresh family alive, so the session rotates on.
    /// If this test ever turns red the hole has been closed and OA-36 can be retired.
    /// </summary>
    [Fact]
    public async Task R6_Ban_Written_Directly_To_BanService_Does_NOT_Revoke_The_Refresh_Family()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);

        var refresh = await MintRefreshTokenAsync(factory);

        // NOT through the gateway admin route — the exact call any other service or an operator
        // with curl can make against ban-service :10065.
        await ban.ApplyTerminalBanAsync(UserId, "red", CancellationToken.None);

        var login = await factory.CreateClient().PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000304", "code": "1234" }"""));
        login.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the gated doors do refuse the account");

        var (status, _) = await RotateAsync(factory, refresh);
        status.Should().Be(HttpStatusCode.OK,
            "OA-36: /v1/auth/refresh consults no suspension source, so a ban that skipped the "
            + "gateway admin route leaves the session rotating indefinitely. This is why the CMS "
            + "route delegates to AdminUsersController instead of writing ban-service directly.");
    }

    // -----------------------------------------------------------------
    // R7 — contract edges the CMS client distinguishes.
    // -----------------------------------------------------------------

    [Fact]
    public async Task R7_Unknown_Action_Is_400_And_Unknown_User_Is_404()
    {
        var ban = new FakeBanService();
        using var factory = MakeFactory(ban);
        var admin = AdminClient(factory);

        (await admin.PatchAsync(CmsRoute, Json("""{ "action": "banish" }""")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await admin.PatchAsync(
            "/user-management/admin/users/99999999-8888-7777-6666-555555555555/status",
            Json("""{ "action": "suspend" }""")))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task R8_Route_Is_Admin_Gated_Exactly_Like_The_Native_Suspend()
    {
        using var factory = MakeFactory(new FakeBanService());

        (await factory.CreateClient().PatchAsync(CmsRoute, Json("""{ "action": "suspend" }""")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var caller = factory.CreateClient();
        caller.DefaultRequestHeaders.Add("X-User-Id", "not-an-admin");
        (await caller.PatchAsync(CmsRoute, Json("""{ "action": "suspend" }""")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// Mints a real session through the OTP door and returns its refresh token.
    private static async Task<string> MintRefreshTokenAsync(WebApplicationFactory<Program> factory)
    {
        var resp = await factory.CreateClient().PostAsync("/v1/auth/otp/verify",
            Json("""{ "phone": "+9613000300", "code": "1234" }"""));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the fixture needs a genuine minted session");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("refreshToken").GetString()!;
    }

    /// Rotates on the real /v1/auth/refresh door and hands back the replacement token.
    private static async Task<(HttpStatusCode Status, string? Rotated)> RotateAsync(
        WebApplicationFactory<Program> factory, string refreshToken)
    {
        var resp = await factory.CreateClient().PostAsync("/v1/auth/refresh",
            Json(JsonSerializer.Serialize(new { refreshToken })));
        if (resp.StatusCode != HttpStatusCode.OK) return (resp.StatusCode, null);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (resp.StatusCode, doc.RootElement.GetProperty("refreshToken").GetString());
    }

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-admin");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>
    /// Production wiring as Program.cs builds it. IAdminUserProjection stays OwnerComposedAdminUsers
    /// and IUserSuspensionSource stays BanServiceUserSuspensionSource; only upstream transports are
    /// doubled, and the ban double is stateful so write and read are the same row.
    /// </summary>
    private static WebApplicationFactory<Program> MakeFactory(IBanServiceClient ban) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtp());
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(new StubDualRole());
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(new StubUmClient(UserId));
                services.RemoveAll<FeedbackClient>();
                services.AddSingleton<FeedbackClient>(new StubFeedbackClient());
                services.RemoveAll<WalletClient>();
                services.AddSingleton<WalletClient>(new StubWalletClient());

                services.RemoveAll<IBanServiceClient>();
                services.AddSingleton(ban);

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    /// <summary>
    /// Stateful stand-in for ban-service: the row the admin suspend writes is the row the mint gate
    /// reads, so no test here can pass by writing and reading two different places.
    /// </summary>
    private sealed class FakeBanService : IBanServiceClient
    {
        private readonly Dictionary<string, BanStatusItem> _bans = new();

        public bool IsBanned(string userId) => _bans.ContainsKey(userId);

        public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
            => Task.FromResult(new BanStatusesResult
            {
                UserId = userId,
                BanStatuses = _bans.TryGetValue(userId, out var row)
                    ? new[] { row }
                    : Array.Empty<BanStatusItem>(),
            });

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
        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(new PhoneFindOrCreateResult(
                UserId, false, new[] { Roles.Client, Roles.Jeeber }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleSwitchReissueResult(userId, "um-access", "um-refresh", opaqueRole));

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client, Roles.Jeeber }, true));

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client }, true));

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserRolesResult?>(
                new UserRolesResult(userId, new[] { Roles.Client, Roles.Jeeber }, Roles.Client));
    }

    /// UM is the identity authority the projection reads; only the known user resolves.
    private sealed class StubUmClient : UmClient
    {
        private readonly string _userId;

        public StubUmClient(string userId) : base("http://localhost", new HttpClient())
            => _userId = userId;

        public override Task<UmProfile> ProfileAsync(string userId, CancellationToken ct)
        {
            if (!string.Equals(userId, _userId, StringComparison.OrdinalIgnoreCase))
            {
                throw new JeebGateway.service.ServiceUserManagement.ApiException(
                    "not found", 404, "{}", EmptyHeaders, null);
            }

            return Task.FromResult(new UmProfile
            {
                UserId = _userId,
                Username = "Suspendable Sam",
                Email = "sam@example.com",
                CreatedDate = "2026-01-05T10:00:00Z",
                Available_roles = new List<string> { Roles.Client, Roles.Jeeber },
                Active_role = Roles.Client,
            });
        }
    }

    /// No reviews — the roster composition must not depend on feedback-service being up.
    private sealed class StubFeedbackClient : FeedbackClient
    {
        public StubFeedbackClient() : base("http://localhost", new HttpClient()) { }

        public override Task<RateeReviewsResponse> RatingsByRateeAsync(
            Guid rateeId, int length, int offset, CancellationToken ct)
            => Task.FromResult(new RateeReviewsResponse
            {
                RateeId = rateeId,
                Reviews = new List<JeebGateway.service.ServiceFeedback.RateeReviewItem>(),
                TotalReviewCount = 0,
                AverageRating = 0,
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
}
