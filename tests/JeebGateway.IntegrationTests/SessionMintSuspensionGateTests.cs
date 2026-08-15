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
/// TRACK-1 REVIEW. <see cref="AuthOtpSuspendedLoginTests"/> proves the OTP gate against the
/// IN-MEMORY store; PRODUCTION composes <see cref="UpstreamBackedUsersStore"/> (Program.cs,
/// whenever <c>GatewayPostgres:ConnectionString</c> is set — every live environment). That store
/// is engineered NEVER to throw: a durable-projection fault is swallowed and the read degrades to
/// a cold path that CANNOT see suspension (the user-management profile contract carries no
/// suspension field at all). So the shipped gate's fail-CLOSED 503 was unreachable in production
/// and a durable-store blip re-opened the measured live defect.
///
/// <para>These tests compose the REAL production store and cover EVERY session-mint path:
/// <list type="bullet">
///   <item>P1 — durable read FAULTS → OTP verify fails CLOSED (503), nothing minted.</item>
///   <item>P2 — CONTROL: durable read healthy → the suspension is enforced (403) from the
///     durable row alone, with the in-process store cold (post-bounce).</item>
///   <item>P3 — CONTROL: an unknown user during a HEALTHY durable read still signs in — the
///     gate refuses on INDETERMINATE status, not on absence, so first-time login still works.</item>
///   <item>P4 — role/switch re-mints a full session, so it carries the same gate.</item>
///   <item>P5 — the email/password facade mints the same session, so it carries the same gate.</item>
/// </list></para>
/// </summary>
public sealed class SessionMintSuspensionGateTests
{
    private const string AppId = "jeeb-test-app";
    private const string Reason = "Policy violation — case 4471";

    // A UM-canonical id: the durable projection is Guid-keyed, so the production
    // read path is only exercised with a well-formed id.
    private const string UserId = "11111111-2222-3333-4444-555555555555";

    // -----------------------------------------------------------------
    // P1 — the durable moderation read FAULTS → FAIL CLOSED.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P1_OtpVerify_DurableModerationReadFaults_FailsClosed_AndMintsNothing()
    {
        var projection = SuspendedProjection();
        projection.FaultReads = true;   // a GatewayPostgres blip: pool exhaustion / timeout / failover
        using var factory = MakeFactory(projection);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json($$"""{ "phone": "+9613000201", "code": "1234" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the production store SWALLOWS a durable-projection fault and degrades to a cold read "
            + "that cannot see suspension — so the gate must not treat that read as authoritative");
        raw.ToLowerInvariant().Should().NotContain("accesstoken",
            "a session minted while account status is unknowable is the measured live defect again");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");

        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/moderation_unavailable");
    }

    // -----------------------------------------------------------------
    // P2 — CONTROL: healthy durable read, in-process store cold → 403.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P2_OtpVerify_SuspendedInDurableProjection_IsRefused_EvenWithColdInMemoryStore()
    {
        using var factory = MakeFactory(SuspendedProjection());
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json($$"""{ "phone": "+9613000202", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "after a bounce the in-process store is empty; the durable row is the only witness");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("detail").GetString().Should().Be(Reason);
    }

    // -----------------------------------------------------------------
    // P3 — CONTROL: absence is NOT indeterminacy; a new user still signs in.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P3_OtpVerify_UnknownUser_HealthyDurableRead_Still_Mints()
    {
        using var factory = MakeFactory(new FakeUserProjectionStore());
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            Json($$"""{ "phone": "+9613000203", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a first-time login must not be refused — the gate closes on an UNREADABLE status, "
            + "never on a genuinely absent row");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("accessToken").GetString()!.Split('.').Should().HaveCount(3);
    }

    // -----------------------------------------------------------------
    // P4 — role/switch re-mints a session, so it must carry the gate.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P4_RoleSwitch_SuspendedAccount_Is_Refused_And_ReMints_Nothing()
    {
        var um = new StubDualRole
        {
            RoleSwitch = new RoleSwitchReissueResult(UserId, "um-access", "um-refresh", Roles.Jeeber),
        };
        using var factory = MakeFactory(SuspendedProjection(), um);
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, UserId, Roles.Client, Roles.Jeeber));

        var resp = await http.PostAsync("/v1/users/me/role/switch", Json("""{ "role": "jeeber" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "admin suspend revokes only REFRESH tokens; the live stateless access token stays valid "
            + "for its remaining TTL, and this route mints a FRESH family that is not in the revoked "
            + "set — so suspension is evaded indefinitely unless the re-mint is gated");
        raw.Should().NotContain("\"accessToken\":\"ey",
            "no fresh signed session may be re-minted for a suspended account");
        um.RoleSwitchCalls.Should().Be(0,
            "the refusal must precede the upstream role mutation, not follow it");
    }

    // -----------------------------------------------------------------
    // P4b — the SECOND re-mint on the same controller (role/unregister).
    // -----------------------------------------------------------------

    [Fact]
    public async Task P4b_RoleUnregister_SuspendedAccount_Is_Refused_And_ReMints_Nothing()
    {
        // The wallet guard must PASS (no provisioned wallet) so the path actually reaches
        // the re-mint — otherwise the test would pass on an unrelated 503.
        using var factory = MakeFactory(SuspendedProjection(), wallet: new StubWalletClient());
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", MintGatewayBearer(factory, UserId, Roles.Client, Roles.Jeeber));

        var resp = await http.PostAsync("/v1/users/me/role/unregister", Json("{}"));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "role/unregister re-mints the SAME access+refresh pair as role/switch, so gating only "
            + "the switch leaves an equivalent rotation route open to a suspended account");
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("\"accessToken\":\"ey");
    }

    // -----------------------------------------------------------------
    // P5 — the email/password facade mints the same session.
    // -----------------------------------------------------------------

    [Fact]
    public async Task P5_EmailLogin_SuspendedAccount_Is_Refused_AndMintsNothing()
    {
        using var factory = MakeFactory(SuspendedProjection(), umClient: new StubUmClient(UserId));
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/login",
            Json("""{ "email": "banned@example.com", "password": "correct-horse" }"""));

        var raw = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the email funnel mints the IDENTICAL gateway session as OTP verify — gating only the "
            + "OTP leg leaves a second, anonymous door into the same account");
        raw.ToLowerInvariant().Should().NotContain("accesstoken");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");
    }

    // -----------------------------------------------------------------
    // fixtures — the PRODUCTION store composition, as Program.cs builds it
    // -----------------------------------------------------------------

    /// A durable projection holding the row an admin suspend persisted, in-process store cold.
    private static FakeUserProjectionStore SuspendedProjection()
    {
        var projection = new FakeUserProjectionStore();
        projection.Seed(new UserProfile
        {
            Id = UserId,
            Phone = string.Empty,
            Name = "Suspended User",
            Roles = new List<string> { Roles.Client, Roles.Jeeber },
            ActiveRole = Roles.Client,
            IsSuspended = true,
            SuspensionReason = Reason,
            SuspendedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return projection;
    }

    private static WebApplicationFactory<Program> MakeFactory(
        FakeUserProjectionStore projection,
        StubDualRole? dualRole = null,
        UmClient? umClient = null,
        WalletClient? wallet = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtp());
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(dualRole ?? new StubDualRole());
                if (umClient is not null)
                {
                    services.RemoveAll<UmClient>();
                    services.AddSingleton(umClient);
                }
                if (wallet is not null)
                {
                    services.RemoveAll<WalletClient>();
                    services.AddSingleton(wallet);
                }

                // EXACTLY the production wiring of Program.cs's GatewayPostgres branch:
                // UpstreamBackedUsersStore over the durable projection + the in-process store.
                services.RemoveAll<IUsersStore>();
                services.RemoveAll<IUserProjectionStore>();
                services.RemoveAll<IUpstreamUserProfileClient>();
                services.AddSingleton<IUserProjectionStore>(projection);
                services.AddSingleton<IUpstreamUserProfileClient>(new StubUpstreamProfileClient());
                services.AddSingleton<IUsersStore, UpstreamBackedUsersStore>();

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
    /// In-memory stand-in for <c>PostgresUserProjectionStore</c> with the two production
    /// behaviours that matter: reads can FAULT, and an identity upsert PRESERVES suspension
    /// on conflict (the real ON CONFLICT clause).
    /// </summary>
    private sealed class FakeUserProjectionStore : IUserProjectionStore
    {
        private readonly Dictionary<string, UserProfile> _rows = new(StringComparer.OrdinalIgnoreCase);

        public bool FaultReads { get; set; }

        public void Seed(UserProfile p) => _rows[p.Id] = p;

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
            => FaultReads
                ? throw new InvalidOperationException("57P01: terminating connection due to administrator command")
                : Task.FromResult(_rows.TryGetValue(userId, out var p) ? p : null);

        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
            => Task.FromResult(new UserSearchResult { Items = _rows.Values.ToList(), Total = _rows.Count });

        public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
        {
            // A COPY, never the caller's instance — a real UPDATE cannot reach back
            // into the object the caller is about to return.
            _rows.TryGetValue(profile.Id, out var existing);
            _rows[profile.Id] = new UserProfile
            {
                Id = profile.Id,
                Phone = profile.Phone,
                Email = profile.Email,
                Name = profile.Name,
                Roles = profile.Roles.ToList(),
                ActiveRole = profile.ActiveRole,
                CreatedAt = profile.CreatedAt,
                UpdatedAt = profile.UpdatedAt,
                // suspension is PRESERVED on conflict, exactly like the real ON CONFLICT clause
                IsSuspended = existing?.IsSuspended ?? profile.IsSuspended,
                SuspensionReason = existing?.SuspensionReason ?? profile.SuspensionReason,
                SuspendedAt = existing?.SuspendedAt ?? profile.SuspendedAt,
            };
            return Task.CompletedTask;
        }

        public Task SetSuspensionAsync(
            string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
        {
            // UPDATE-only, exactly like the real store: absent row => nothing persisted.
            if (_rows.TryGetValue(userId, out var row))
            {
                row.IsSuspended = isSuspended;
                row.SuspensionReason = isSuspended ? reason : null;
                row.SuspendedAt = isSuspended ? at : null;
            }
            return Task.CompletedTask;
        }

        public Task PurgePiiAsync(string userId, CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>
    /// Mirrors <c>ScopedUserManagementProfileClient.Map</c>: the UM profile contract
    /// (<c>UserProfileResponse</c>) carries NO suspension field, so a cold read can only ever
    /// manufacture a NOT-suspended profile. This is why the cold path cannot police moderation.
    /// </summary>
    private sealed class StubUpstreamProfileClient : IUpstreamUserProfileClient
    {
        public Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserProfile?>(new UserProfile
            {
                Id = userId,
                Phone = string.Empty,
                Name = "Suspended User",
                Roles = new List<string> { Roles.Client },
                ActiveRole = Roles.Client,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
                UpdatedAt = DateTimeOffset.UtcNow,
            });
    }

    private sealed class StubDualRole : IUserManagementDualRoleClient
    {
        public RoleSwitchReissueResult? RoleSwitch { get; init; }
        public int RoleSwitchCalls { get; private set; }

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
            => Task.FromResult(new PhoneFindOrCreateResult(UserId, false, new[] { Roles.Client }, Roles.Client));

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(string userId, string opaqueRole, CancellationToken ct)
        {
            RoleSwitchCalls++;
            return Task.FromResult(RoleSwitch ?? new RoleSwitchReissueResult(userId, "a", "r", opaqueRole));
        }

        public Task<RoleGrantResult> AppendAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client }, false));

        public Task<RoleGrantResult> RemoveAvailableRoleAsync(string userId, string opaqueRole, CancellationToken ct)
            => Task.FromResult(new RoleGrantResult(userId, new[] { Roles.Client }, false));

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserRolesResult?>(null);
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

    /// No wallet provisioned — the 404 the controller reads as an honest zero balance.
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
