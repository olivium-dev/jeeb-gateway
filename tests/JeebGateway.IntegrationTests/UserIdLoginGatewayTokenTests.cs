using System.Collections.Concurrent;
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
using Microsoft.Extensions.Logging;
using Xunit;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmListResponse = JeebGateway.service.ServiceUserManagement.GetAllUsersResponse;
using UmLoginRequest = JeebGateway.service.ServiceUserManagement.UserIdLoginRequest;
using UmLoginResponse = JeebGateway.service.ServiceUserManagement.SocialLoginResponse;
using UmProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;
using UmRolesResponse = JeebGateway.service.ServiceUserManagement.UserRolesResponse;

namespace JeebGateway.IntegrationTests;

public sealed class UserIdLoginGatewayTokenTests
{
    private const string UserId = "2c6e3dc9-a332-4d29-8c32-829f84c4c5f1";
    private const string OtherUserId = "01f50e52-5227-4f1f-99f5-4968694e265a";
    private const string ThirdUserId = "c307fa89-8391-4e27-91e8-d571b8f3305f";
    private const string FourthUserId = "864d2042-e397-4ae1-9c92-2b1825f30a4a";
    private const string LegacyPasscode = "test-passcode";

    [Theory]
    [InlineData("/api/User/user-id-login", true)]
    [InlineData("/api/User/userid-login", false)]
    public async Task OpenMode_BothAliases_IgnoreStaleOrOmittedPasscode_AndMintFromCompleteRoster(
        string route,
        bool includeStalePasscode)
    {
        var um = new RecordingUmClient
        {
            AllPages =
            [
                Page(hasMore: false, Profile(UserId, Roles.Client, Roles.Jeeber)),
            ],
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);
        object request = includeStalePasscode
            ? new { userId = $"  {UserId}  ", superAdminPassCode = "stale-and-ignored" }
            : new { userId = $"  {UserId}  " };

        var response = await factory.CreateClient().PostAsJsonAsync(route, request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UmLoginResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be(UserId);
        body.AuthToken.Should().Be(RecordingTokenService.AccessToken);
        body.RefreshToken.Should().Be(RecordingTokenService.RefreshToken);
        body.RecentlyCreated.Should().BeFalse();
        um.UserIdLoginCalls.Should().Be(0,
            "OpenMode must not call the passcode-gated legacy login operation");
        um.RolesCalls.Should().Be(0,
            "OpenMode authority comes from the same opaque-identity roster as the picker");
        um.AllCalls.Should().Equal(new AllCall(0, 200, null));
        tokens.Issues.Should().ContainSingle();
        tokens.Issues[0].UserId.Should().Be(UserId);
        tokens.Issues[0].Roles.Should().Equal(Roles.Client, Roles.Jeeber);
        tokens.Issues[0].ActiveRole.Should().Be(Roles.Jeeber);
    }

    [Fact]
    public async Task OpenMode_LaterPageMatch_ScansToCompletion_AndAdvancesByActualRows()
    {
        var um = new RecordingUmClient
        {
            AllPages =
            [
                Page(
                    hasMore: true,
                    Profile(OtherUserId, Roles.Client),
                    Profile(ThirdUserId, Roles.Client)),
                Page(hasMore: true, Profile(UserId, Roles.Client, Roles.Jeeber)),
                Page(hasMore: false, Profile(FourthUserId, Roles.Client)),
            ],
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        um.AllCalls.Should().Equal(
            new AllCall(0, 200, null),
            new AllCall(2, 200, null),
            new AllCall(3, 200, null));
        tokens.Issues.Should().ContainSingle();
    }

    [Fact]
    public async Task OpenMode_RealTokenService_IssuesGatewayAudienceSession()
    {
        var um = new RecordingUmClient
        {
            AllPages =
            [
                Page(hasMore: false, Profile(UserId, Roles.Client, Roles.Jeeber)),
            ],
        };
        using var factory = MakeRealTokenFactory(um);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<UmLoginResponse>();
        body!.RefreshToken.Should().NotBeNullOrWhiteSpace();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body.AuthToken);
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
        um.AllCalls.Should().BeEmpty();
        um.RolesCalls.Should().Be(0);
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
        um.AllCalls.Should().BeEmpty();
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
        um.AllCalls.Should().BeEmpty();
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_MissingIdentity_ReturnsSanitized404OnlyAfterCompleteScan()
    {
        var um = new RecordingUmClient
        {
            AllPages =
            [
                Page(hasMore: true, Profile(OtherUserId, Roles.Client)),
                Page(hasMore: false, Profile(ThirdUserId, Roles.Jeeber)),
            ],
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(UserId);
        um.AllCalls.Should().Equal(
            new AllCall(0, 200, null),
            new AllCall(1, 200, null));
        tokens.Issues.Should().BeEmpty();
        um.UserIdLoginCalls.Should().Be(0);
    }

    [Fact]
    public async Task OpenMode_LaterDuplicateIdentity_Returns502WithoutMinting()
    {
        var um = new RecordingUmClient
        {
            AllPages =
            [
                Page(hasMore: true, Profile(UserId, Roles.Client)),
                Page(hasMore: true, Profile(OtherUserId, Roles.Client)),
                Page(hasMore: false, Profile(UserId, Roles.Client)),
            ],
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        um.AllCalls.Should().HaveCount(3,
            "the scan must reach authoritative completion after the first match");
        tokens.Issues.Should().BeEmpty();
    }

    public static IEnumerable<object[]> MalformedBatches()
    {
        yield return new object[]
        {
            "null batch",
            new UmListResponse?[] { null },
        };
        yield return new object[]
        {
            "null users",
            new UmListResponse?[] { new UmListResponse { Users = null, HasMore = false } },
        };
        yield return new object[]
        {
            "empty non-terminal batch",
            new UmListResponse?[] { Page(hasMore: true) },
        };
        yield return new object[]
        {
            "null roster row",
            new UmListResponse?[] { Page(hasMore: false, (UmProfile)null!) },
        };
    }

    [Theory]
    [MemberData(nameof(MalformedBatches))]
    public async Task OpenMode_MalformedRosterBatch_Returns502WithoutMinting(
        string reason,
        IReadOnlyList<UmListResponse?> pages)
    {
        var um = new RecordingUmClient { AllPages = pages };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway, reason);
        um.AllCalls.Should().ContainSingle();
        tokens.Issues.Should().BeEmpty();
    }

    public static IEnumerable<object[]> InvalidAuthorityRecords()
    {
        yield return new object[] { "empty roles", Profile(UserId) };
        yield return new object[] { "blank role", Profile(UserId, " ") };
        yield return new object[] { "untrimmed role", Profile(UserId, " customer ") };
        yield return new object[] { "duplicate role", Profile(UserId, Roles.Client, "CUSTOMER") };
        yield return new object[]
        {
            "missing active role",
            Profile(UserId, new[] { Roles.Client }, activeRole: null),
        };
        yield return new object[]
        {
            "active role not held",
            Profile(UserId, new[] { Roles.Client }, activeRole: Roles.Jeeber),
        };
        yield return new object[]
        {
            "untrimmed active role",
            Profile(UserId, new[] { Roles.Client }, activeRole: $" {Roles.Client}"),
        };
    }

    [Theory]
    [MemberData(nameof(InvalidAuthorityRecords))]
    public async Task OpenMode_InconsistentAuthoritativeRoles_Returns502WithoutMinting(
        string reason,
        UmProfile profile)
    {
        var um = new RecordingUmClient
        {
            AllPages = [Page(hasMore: false, profile)],
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway, reason);
        um.AllCalls.Should().ContainSingle();
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_HasMoreAtPageCap_Returns502WithoutMinting()
    {
        var pages = Enumerable.Range(0, 100)
            .Select(index => (UmListResponse?)Page(
                hasMore: true,
                Profile($"00000000-0000-4000-8000-{index:D12}", Roles.Client)))
            .ToArray();
        var um = new RecordingUmClient { AllPages = pages };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        um.AllCalls.Should().HaveCount(100);
        um.AllCalls.Select(call => call.Skip).Should().Equal(
            Enumerable.Range(0, 100).Select(value => (int?)value));
        um.AllCalls.Should().OnlyContain(call => call.Limit == 200);
        tokens.Issues.Should().BeEmpty();
    }

    [Theory]
    [InlineData("/api/User/user-id-login")]
    [InlineData("/api/User/userid-login")]
    public async Task OpenMode_AllAsyncApiFailure_IsSanitized502AndDoesNotLeakOrMint(
        string route)
    {
        const string canary = "SECRET_CANARY_openmode_roster_response";
        var um = new RecordingUmClient
        {
            AllFailure = ApiFailure(StatusCodes.Status503ServiceUnavailable, canary),
        };
        var tokens = new RecordingTokenService();
        using var logs = new CapturingLoggerProvider();
        using var factory = MakeFactory(openMode: true, um, tokens, loggerProvider: logs);

        var response = await factory.CreateClient().PostAsJsonAsync(
            route, new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(canary);
        logs.Entries.Should().Contain(entry =>
            entry.Contains("roster authority failed", StringComparison.Ordinal));
        logs.Entries.Should().NotContain(entry => entry.Contains(canary, StringComparison.Ordinal));
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_UnexpectedAllAsyncFault_IsSanitized502AndDoesNotLeakOrMint()
    {
        const string canary = "SECRET_CANARY_transport_exception";
        var um = new RecordingUmClient
        {
            AllFailure = new HttpRequestException(canary),
        };
        var tokens = new RecordingTokenService();
        using var logs = new CapturingLoggerProvider();
        using var factory = MakeFactory(openMode: true, um, tokens, loggerProvider: logs);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/user-id-login", new { userId = UserId });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain(canary);
        logs.Entries.Should().NotContain(entry => entry.Contains(canary, StringComparison.Ordinal));
        tokens.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenMode_CancellationIndependentOfRequest_Returns502WithoutMinting()
    {
        var um = new RecordingUmClient
        {
            AllFailure = new OperationCanceledException("upstream timeout"),
        };
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
        };
        var tokens = new RecordingTokenService();
        using var factory = MakeFactory(openMode: true, um, tokens);

        var response = await factory.CreateClient().PostAsJsonAsync(
            "/api/User/login/userId",
            new { userId = UserId, superAdminPassCode = LegacyPasscode });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        um.UserIdLoginCalls.Should().Be(1);
        um.LastLoginRequest!.SuperAdminPassCode.Should().Be(LegacyPasscode);
        um.AllCalls.Should().BeEmpty();
        um.RolesCalls.Should().Be(0);
        tokens.Issues.Should().BeEmpty();
    }

    private static WebApplicationFactory<Program> MakeFactory(
        bool openMode,
        RecordingUmClient um,
        RecordingTokenService tokens,
        TestUserManagementDualRoleClient? legacyRoles = null,
        ILoggerProvider? loggerProvider = null)
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
                if (loggerProvider is not null)
                {
                    services.AddSingleton(loggerProvider);
                }
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

    private static UmProfile Profile(string? userId, params string[] roles)
        => Profile(userId, roles, roles.LastOrDefault());

    private static UmProfile Profile(
        string? userId,
        IReadOnlyCollection<string>? roles,
        string? activeRole)
        => new()
        {
            UserId = userId,
            Username = userId,
            Available_roles = roles?.ToArray(),
            Active_role = activeRole,
        };

    private static UmListResponse Page(bool hasMore, params UmProfile[] profiles)
        => new()
        {
            Users = profiles,
            HasMore = hasMore,
            Limit = 200,
            TotalCount = profiles.Length,
        };

    private static UmApiException ApiFailure(
        int status,
        string response = "sensitive-upstream-body")
        => new(
            "upstream failure",
            status,
            response,
            new Dictionary<string, IEnumerable<string>>(),
            null);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entries = new();

        internal IReadOnlyCollection<string> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(ConcurrentQueue<string> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                entries.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    entries.Enqueue(exception.ToString());
                }
            }
        }

        private sealed class NullScope : IDisposable
        {
            internal static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }

    private sealed class RecordingUmClient : UmClient
    {
        internal RecordingUmClient()
            : base("http://user-management.test", new HttpClient())
        {
        }

        internal int UserIdLoginCalls { get; private set; }
        internal int RolesCalls { get; private set; }
        internal List<AllCall> AllCalls { get; } = new();
        internal UmLoginRequest? LastLoginRequest { get; private set; }
        internal UmLoginResponse LoginResponse { get; init; } = new()
        {
            UserId = UserId,
            RecentlyCreated = false,
        };
        internal IReadOnlyList<UmListResponse?> AllPages { get; init; } =
        [
            UserIdLoginGatewayTokenTests.Page(
                hasMore: false,
                UserIdLoginGatewayTokenTests.Profile(UserId, Roles.Client)),
        ];
        internal Exception? LoginFailure { get; init; }
        internal Exception? AllFailure { get; init; }
        internal int AllFailureOnCall { get; init; } = 1;

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

        public override Task<UmListResponse> AllAsync(
            int? skip,
            int? limit,
            bool? onActive,
            CancellationToken cancellationToken)
        {
            AllCalls.Add(new AllCall(skip, limit, onActive));
            var callNumber = AllCalls.Count;
            if (AllFailure is not null && callNumber == AllFailureOnCall)
            {
                return Task.FromException<UmListResponse>(AllFailure);
            }

            var pageIndex = callNumber - 1;
            if (pageIndex >= AllPages.Count)
            {
                return Task.FromException<UmListResponse>(
                    new InvalidOperationException("Unexpected extra AllAsync page request."));
            }

            return Task.FromResult(AllPages[pageIndex]!);
        }

        public override Task<UmRolesResponse> RolesAsync(
            string userId,
            CancellationToken cancellationToken)
        {
            RolesCalls++;
            return Task.FromException<UmRolesResponse>(
                new InvalidOperationException("RolesAsync must not be used for OpenMode authority."));
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

    private sealed record AllCall(int? Skip, int? Limit, bool? OnActive);

    private sealed record IssueRecord(
        string UserId,
        IReadOnlyList<string> Roles,
        string ActiveRole);
}
