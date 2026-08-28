using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmLoginRequest = JeebGateway.service.ServiceUserManagement.UserIdLoginRequest;
using UmLoginResponse = JeebGateway.service.ServiceUserManagement.SocialLoginResponse;
using UmRolesResponse = JeebGateway.service.ServiceUserManagement.UserRolesResponse;

namespace JeebGateway.IntegrationTests;

public sealed class UserIdLoginGatewayTokenTests
{
    private const string UserId = "super-login-gateway-audience-user";
    private const string LegacyPasscode = "test-passcode";

    [Theory]
    [InlineData("/api/User/user-id-login", "deliberately-ignored")]
    [InlineData("/api/User/userid-login", null)]
    public async Task OpenMode_BothAliases_IgnorePasscode_AndMintFromAuthoritativeRoles(
        string route,
        string? passcode)
    {
        var um = new RecordingUmClient
        {
            RolesResponse = RolesResponse(UserId, Roles.Client, Roles.Jeeber),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(route, new
        {
            userId = $"  {UserId}  ",
            superAdminPassCode = passcode,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UmLoginResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be(UserId);
        body.AuthToken.Should().Be(RecordingTokenService.AccessToken);
        body.RefreshToken.Should().Be(RecordingTokenService.RefreshToken);
        body.RecentlyCreated.Should().BeFalse();

        um.UserIdLoginCalls.Should().Be(0,
            "OpenMode must not call the passcode-gated legacy login operation");
        um.RolesCalls.Should().Be(1);
        um.LastRolesUserId.Should().Be(UserId, "the request userId is trimmed before authority lookup");
        tokens.Issues.Should().ContainSingle();
        tokens.Issues[0].UserId.Should().Be(UserId);
        tokens.Issues[0].Roles.Should().Equal(Roles.Client, Roles.Jeeber);
        tokens.Issues[0].ActiveRole.Should().Be(Roles.Jeeber);
    }

    [Fact]
    public async Task OpenMode_RealTokenService_IssuesGatewayAudienceSession()
    {
        var um = new RecordingUmClient
        {
            RolesResponse = RolesResponse(UserId, Roles.Client, Roles.Jeeber),
        };
        using var factory = MakeRealTokenFactory(um);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UmLoginResponse>();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.AuthToken);
        jwt.Issuer.Should().Be("jeeb-gateway");
        jwt.Audiences.Should().Contain("jeeb-clients");
        jwt.Subject.Should().Be(UserId);
        jwt.Claims.Where(claim => claim.Type == "roles").Select(claim => claim.Value)
            .Should().Equal(Roles.Client, Roles.Jeeber);
        jwt.Claims.Single(claim => claim.Type == "active_role").Value
            .Should().Be(Roles.Jeeber);
    }

    [Theory]
    [InlineData("/api/User/user-id-login")]
    [InlineData("/api/User/userid-login")]
    public async Task ClosedMode_BothAliases_PreserveLegacyPasscodeCallAndGatewayRemint(
        string route)
    {
        var um = new RecordingUmClient
        {
            LoginResponse = new UmLoginResponse
            {
                UserId = UserId,
                AuthToken = "upstream-user-management-access",
                RefreshToken = "upstream-user-management-refresh",
                RecentlyCreated = false,
            },
        };
        var legacyRoles = new TestUserManagementDualRoleClient();
        legacyRoles.Seed(UserId, new[] { Roles.Client, Roles.Jeeber }, Roles.Jeeber);
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(
            openMode: false, um, tokens, legacyRoles);

        var response = await factory.CreateClient().PostAsJsonAsync(route, new
        {
            userId = UserId,
            superAdminPassCode = LegacyPasscode,
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UmLoginResponse>();
        body!.AuthToken.Should().Be(RecordingTokenService.AccessToken);
        body.RefreshToken.Should().Be(RecordingTokenService.RefreshToken);
        um.UserIdLoginCalls.Should().Be(1);
        um.LastLoginRequest!.UserId.Should().Be(UserId);
        um.LastLoginRequest.SuperAdminPassCode.Should().Be(LegacyPasscode);
        um.RolesCalls.Should().Be(0,
            "closed mode preserves the incumbent passcode path and its role adapter");
        tokens.Issues.Should().ContainSingle();
        tokens.Issues[0].Roles.Should().Equal(Roles.Client, Roles.Jeeber);
        tokens.Issues[0].ActiveRole.Should().Be(Roles.Jeeber);
    }

    [Fact]
    public async Task ClosedMode_PasscodeRejection_Remains401AndDoesNotMint()
    {
        var um = new RecordingUmClient
        {
            LoginFailure = ApiFailure(StatusCodes.Status401Unauthorized),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: false, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login",
            new { userId = UserId, superAdminPassCode = "wrong-passcode" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("sensitive-upstream-body");
        um.UserIdLoginCalls.Should().Be(1);
        um.RolesCalls.Should().Be(0);
        tokens.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/User/user-id-login", null)]
    [InlineData("/api/User/user-id-login", "")]
    [InlineData("/api/User/user-id-login", "   ")]
    [InlineData("/api/User/userid-login", "\t\r\n")]
    public async Task OpenMode_BlankUserId_Returns400WithoutAuthorityOrToken(
        string route,
        string? userId)
    {
        var um = new RecordingUmClient();
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(route, new
        {
            userId,
            superAdminPassCode = (string?)null,
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Type.Should().Be("https://jeeb.dev/errors/user-id-required");
        um.UserIdLoginCalls.Should().Be(0);
        um.RolesCalls.Should().Be(0);
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_UnknownUser_PreservesSanitized404WithoutMinting()
    {
        var um = new RecordingUmClient
        {
            RolesFailure = ApiFailure(StatusCodes.Status404NotFound),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("sensitive-upstream-body");
        tokens.Issues.Should().BeEmpty();
        um.UserIdLoginCalls.Should().Be(0);
    }

    [Fact]
    public async Task OpenMode_UpstreamAuthorityFailure_PreservesSanitizedStatusWithoutMinting()
    {
        var um = new RecordingUmClient
        {
            RolesFailure = ApiFailure(StatusCodes.Status503ServiceUnavailable),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("sensitive-upstream-body");
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_UnexpectedAuthorityFailure_Returns502WithoutMinting()
    {
        var um = new RecordingUmClient
        {
            RolesFailure = new HttpRequestException("transport failed"),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        tokens.Issues.Should().BeEmpty();
    }

    public static IEnumerable<object[]> InvalidAuthorityRecords()
    {
        yield return new object[] { "mismatched identity", RolesResponse("another-user", Roles.Client) };
        yield return new object[] { "missing identity", RolesResponse(null, Roles.Client) };
        yield return new object[] { "empty roles", RolesResponse(UserId) };
        yield return new object[] { "blank role", RolesResponse(UserId, " ") };
        yield return new object[] { "untrimmed role", RolesResponse(UserId, " customer ") };
        yield return new object[] { "duplicate role", RolesResponse(UserId, Roles.Client, "CUSTOMER") };
        yield return new object[]
        {
            "missing active role",
            RolesResponse(UserId, new[] { Roles.Client }, activeRole: null),
        };
        yield return new object[]
        {
            "active role not held",
            RolesResponse(UserId, new[] { Roles.Client }, activeRole: Roles.Jeeber),
        };
        yield return new object[]
        {
            "untrimmed active role",
            RolesResponse(UserId, new[] { Roles.Client }, activeRole: $" {Roles.Client}"),
        };
    }

    [Theory]
    [MemberData(nameof(InvalidAuthorityRecords))]
    public async Task OpenMode_MalformedOrMismatchedAuthority_Returns502WithoutMinting(
        string reason,
        UmRolesResponse roles)
    {
        var um = new RecordingUmClient { RolesResponse = roles };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway, reason);
        um.RolesCalls.Should().Be(1);
        um.UserIdLoginCalls.Should().Be(0);
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_NullAuthorityResponse_Returns502WithoutMinting()
    {
        var um = new RecordingUmClient { RolesResponse = null };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_DoesNotChangeLegacyLoginUserIdRoute()
    {
        var um = new RecordingUmClient
        {
            LoginResponse = new UmLoginResponse { UserId = UserId },
            RolesFailure = new InvalidOperationException("must not be called"),
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/login/userId",
            new { userId = UserId, superAdminPassCode = LegacyPasscode });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        um.UserIdLoginCalls.Should().Be(1);
        um.LastLoginRequest!.SuperAdminPassCode.Should().Be(LegacyPasscode);
        um.RolesCalls.Should().Be(0);
        tokens.Issues.Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> MakeFactory(
        bool openMode,
        RecordingUmClient um,
        RecordingTokenService tokens,
        TestUserManagementDualRoleClient? legacyRoles = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SuperLogin:OpenMode"] = openMode ? "true" : "false",
                    ["Security:RateLimit:Enabled"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
                services.RemoveAll<ITokenService>();
                services.AddSingleton<ITokenService>(tokens);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(
                    legacyRoles ?? new TestUserManagementDualRoleClient());
            });
        });

    private static WebApplicationFactory<Program> MakeRealTokenFactory(RecordingUmClient um)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SuperLogin:OpenMode"] = "true",
                    ["Security:RateLimit:Enabled"] = "false",
                }));
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(
                    new TestUserManagementDualRoleClient());
            });
        });

    private static UmRolesResponse RolesResponse(string? userId, params string[] roles)
        => RolesResponse(userId, roles, roles.LastOrDefault());

    private static UmRolesResponse RolesResponse(
        string? userId,
        IReadOnlyCollection<string> roles,
        string? activeRole)
        => new()
        {
            UserId = userId,
            Available_roles = roles.ToArray(),
            Active_role = activeRole,
        };

    private static UmApiException ApiFailure(int status)
        => new(
            "upstream failure",
            status,
            "sensitive-upstream-body",
            new Dictionary<string, IEnumerable<string>>(),
            null);

    private sealed class RecordingUmClient : UmClient
    {
        internal RecordingUmClient()
            : base("http://user-management.test", new HttpClient())
        {
        }

        internal int UserIdLoginCalls { get; private set; }
        internal int RolesCalls { get; private set; }
        internal UmLoginRequest? LastLoginRequest { get; private set; }
        internal string? LastRolesUserId { get; private set; }
        internal UmLoginResponse LoginResponse { get; init; } = new()
        {
            UserId = UserId,
            RecentlyCreated = false,
        };
        internal UmRolesResponse? RolesResponse { get; init; } =
            UserIdLoginGatewayTokenTests.RolesResponse(UserId, Roles.Client);
        internal Exception? LoginFailure { get; init; }
        internal Exception? RolesFailure { get; init; }

        public override Task<UmLoginResponse> UserIdLoginAsync(
            UmLoginRequest? body,
            CancellationToken cancellationToken)
        {
            UserIdLoginCalls++;
            LastLoginRequest = body;
            return LoginFailure is null
                ? Task.FromResult(LoginResponse)
                : Task.FromException<UmLoginResponse>(LoginFailure);
        }

        public override Task<UmRolesResponse> RolesAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            RolesCalls++;
            LastRolesUserId = userId;
            return RolesFailure is null
                ? Task.FromResult(RolesResponse!)
                : Task.FromException<UmRolesResponse>(RolesFailure);
        }
    }

    private sealed class RecordingTokenService : ITokenService
    {
        internal const string AccessToken = "gateway-access-token";
        internal const string RefreshToken = "gateway-refresh-token";

        internal List<IssueRecord> Issues { get; } = new();

        public Task<TokenPair> IssueAsync(
            string userId,
            IEnumerable<string> roles,
            CancellationToken ct)
            => IssueAsync(
                userId,
                roles,
                roles.FirstOrDefault() ?? string.Empty,
                authentication: null,
                ct);

        public Task<TokenPair> IssueAsync(
            string userId,
            IEnumerable<string> roles,
            string activeRole,
            VerifiedAuthenticationContext? authentication,
            CancellationToken ct)
        {
            Issues.Add(new IssueRecord(userId, roles.ToArray(), activeRole));
            return Task.FromResult(new TokenPair
            {
                AccessToken = AccessToken,
                RefreshToken = RefreshToken,
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
            });
        }

        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RevokeAsync(
            string refreshToken,
            RevocationReason reason,
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> RevokeAllForUserAsync(
            string userId,
            RevocationReason reason,
            CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed record IssueRecord(
        string UserId,
        IReadOnlyList<string> Roles,
        string ActiveRole);
}
