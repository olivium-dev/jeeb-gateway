using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.Moderation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Security regression pack for the OTP-to-session trust boundary. OTP validation proves
/// possession of a phone; only user-management plus the suspension authority may establish
/// which identity and roles receive a session. Every uncertain authority result is therefore
/// a typed 503 with no local identity, projection, token, or success-log side effect.
/// </summary>
public sealed class AuthOtpIdentityAuthorityFailClosedTests
{
    private const string AppId = "jeeb-identity-authority-test";
    private const string Phone = "+9613000199";
    private const string Code = "2468";
    private const string UserId = "41a864a2-42c6-4e0c-8ecb-0878df34ff07";
    private const string OtherUserId = "9162d99a-10d2-40ab-9027-b6a6cb82647e";

    [Theory]
    [InlineData(UnavailableCase.UserManagementFlagOff)]
    [InlineData(UnavailableCase.UserManagementStatusFault)]
    [InlineData(UnavailableCase.UserManagementDependencyTimeout)]
    [InlineData(UnavailableCase.EmptyIdentity)]
    [InlineData(UnavailableCase.NonCanonicalIdentity)]
    [InlineData(UnavailableCase.MissingRoles)]
    [InlineData(UnavailableCase.RoleStatusFault)]
    [InlineData(UnavailableCase.MismatchedRoleIdentity)]
    [InlineData(UnavailableCase.EmptyRoleIdentity)]
    [InlineData(UnavailableCase.EmptyRoles)]
    [InlineData(UnavailableCase.MalformedRole)]
    [InlineData(UnavailableCase.ActiveRoleNotHeld)]
    [InlineData(UnavailableCase.ModerationUncertain)]
    public async Task Verify_WhenIdentityAuthorityIsUncertain_ReturnsTyped503_AndHasNoMintSideEffects(
        UnavailableCase scenario)
    {
        var fixture = AuthorityFixture.For(scenario);
        await using var factory = MakeFactory(fixture);
        using var http = factory.CreateClient();

        using var response = await http.PostAsync(
            "/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{Phone}}", "code": "{{Code}}" }"""));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(503);
        problem.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/identity_unavailable");
        problem.RootElement.GetProperty("detail").GetString().Should()
            .Be("Identity and account status could not be verified. Please try again.");
        problem.RootElement.TryGetProperty("accessToken", out _).Should().BeFalse();
        problem.RootElement.TryGetProperty("refreshToken", out _).Should().BeFalse();

        fixture.Otp.ValidateCalls.Should().Be(1,
            "identity authority is consulted only after the shared OTP service validates possession");
        fixture.Users.GetOrCreateCalls.Should().Be(0,
            "a phone-derived local identity is forbidden even when user-management is unavailable");
        fixture.Users.ProjectionWrites.Should().Be(0);
        fixture.Users.OtherMutationCalls.Should().Be(0);
        fixture.Tokens.IssueCalls.Should().Be(0);

        AssertNoSensitiveOrSuccessLog(fixture.Logs);
    }

    [Fact]
    public async Task Verify_WhenCanonicalIdentityIsSuspended_Preserves403_AndMintsNothing()
    {
        var fixture = AuthorityFixture.Valid();
        fixture.Suspensions.Result = new UserSuspension(true, "Policy restriction");
        await using var factory = MakeFactory(fixture);
        using var http = factory.CreateClient();

        using var response = await http.PostAsync(
            "/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{Phone}}", "code": "{{Code}}" }"""));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/account_suspended");
        fixture.Users.GetOrCreateCalls.Should().Be(0);
        fixture.Users.ProjectionWrites.Should().Be(0);
        fixture.Users.OtherMutationCalls.Should().Be(0);
        fixture.Tokens.IssueCalls.Should().Be(0);
        AssertNoSensitiveOrSuccessLog(fixture.Logs);
    }

    [Fact]
    public async Task Verify_WhenAllAuthoritiesAreCanonical_PreservesFrozenSuccessContract()
    {
        var fixture = AuthorityFixture.Valid();
        await using var factory = MakeFactory(fixture);
        using var http = factory.CreateClient();

        using var response = await http.PostAsync(
            "/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{Phone}}", "code": "{{Code}}" }"""));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(new[] { "accessToken", "refreshToken", "user" });
        var user = body.RootElement.GetProperty("user");
        user.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(new[] { "userId", "active_role", "available_roles" });
        user.GetProperty("userId").GetString().Should().Be(UserId);
        user.GetProperty("active_role").GetString().Should().Be("client");
        user.GetProperty("available_roles").EnumerateArray().Select(value => value.GetString())
            .Should().Equal("client", "jeeber");

        fixture.UserManagement.FindCalls.Should().Be(1);
        fixture.UserManagement.RoleCalls.Should().Be(1);
        fixture.Suspensions.Calls.Should().Be(1);
        fixture.Users.GetOrCreateCalls.Should().Be(0);
        fixture.Users.ProjectionWrites.Should().Be(1);
        fixture.Users.OtherMutationCalls.Should().Be(0);
        fixture.Tokens.IssueCalls.Should().Be(1);
        fixture.Tokens.LastUserId.Should().Be(UserId);
        fixture.Tokens.LastRoles.Should().Equal(Roles.Client, Roles.Jeeber);
        AssertNoSensitiveLog(fixture.Logs);
    }

    [Fact]
    public async Task Request_NonLebaneseNumber_PreservesInvalidCountry_AndCallsNoUpstream()
    {
        var fixture = AuthorityFixture.Valid();
        await using var factory = MakeFactory(fixture);
        using var http = factory.CreateClient();

        using var response = await http.PostAsync(
            "/v1/auth/otp/request",
            JsonBody("""{ "phone": "+12025550123" }"""));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/invalid_country");
        fixture.Otp.SendCalls.Should().Be(0);
        fixture.UserManagement.FindCalls.Should().Be(0);
        fixture.Tokens.IssueCalls.Should().Be(0);
    }

    [Fact]
    public async Task ModerationGate_WhenCallerCancels_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new ControlledSuspensionSource { CancelFromCaller = true };

        var act = () => UserModerationGate.EvaluateAsync(
            source,
            UserId,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static WebApplicationFactory<Program> MakeFactory(AuthorityFixture fixture) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(fixture.Logs);
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(fixture.Otp);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient>(fixture.UserManagement);
                services.RemoveAll<IUserSuspensionSource>();
                services.AddSingleton<IUserSuspensionSource>(fixture.Suspensions);
                services.RemoveAll<IUsersStore>();
                services.AddSingleton<IUsersStore>(fixture.Users);
                services.RemoveAll<ITokenService>();
                services.AddSingleton<ITokenService>(fixture.Tokens);
                services.Configure<UpstreamFeatureFlags>(flags =>
                {
                    flags.Otp = true;
                    flags.UserManagement = fixture.UserManagementEnabled;
                });
                services.Configure<OtpSignInOptions>(options =>
                {
                    options.ApplicationId = AppId;
                    options.TtlSeconds = 300;
                });
            });
        });

    private static void AssertNoSensitiveOrSuccessLog(CapturingLoggerProvider logs)
    {
        AssertNoSensitiveLog(logs);
        logs.Messages.Should().NotContain(message =>
            message.Contains("auth.otp.verify ok", StringComparison.Ordinal));
    }

    private static void AssertNoSensitiveLog(CapturingLoggerProvider logs)
    {
        var messages = string.Join("\n", logs.Messages);
        messages.Should().NotContain(Phone);
        messages.Should().NotContain(Code);
        messages.Should().NotContain(UserId);
        messages.Should().NotContain("access-test-token");
        messages.Should().NotContain("refresh-test-token");
        var normalized = messages.ToLowerInvariant();
        normalized.Should().NotContain("phone");
        normalized.Should().NotContain("code");
        normalized.Should().NotContain("token");
        normalized.Should().NotContain("userid");
        normalized.Should().NotContain("user id");
        normalized.Should().NotContain("body");
        normalized.Should().NotContain("hash");
        normalized.Should().NotContain("length");
        normalized.Should().NotContain("digest");
        normalized.Should().NotContain("fingerprint");
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    public enum UnavailableCase
    {
        UserManagementFlagOff,
        UserManagementStatusFault,
        UserManagementDependencyTimeout,
        EmptyIdentity,
        NonCanonicalIdentity,
        MissingRoles,
        RoleStatusFault,
        MismatchedRoleIdentity,
        EmptyRoleIdentity,
        EmptyRoles,
        MalformedRole,
        ActiveRoleNotHeld,
        ModerationUncertain,
    }

    private sealed class AuthorityFixture
    {
        public required bool UserManagementEnabled { get; init; }
        public required RecordingOtpClient Otp { get; init; }
        public required ControlledUserManagementClient UserManagement { get; init; }
        public required ControlledSuspensionSource Suspensions { get; init; }
        public required RecordingUsersStore Users { get; init; }
        public required RecordingTokenService Tokens { get; init; }
        public required CapturingLoggerProvider Logs { get; init; }

        public static AuthorityFixture Valid() => new()
        {
            UserManagementEnabled = true,
            Otp = new RecordingOtpClient(),
            UserManagement = new ControlledUserManagementClient(),
            Suspensions = new ControlledSuspensionSource(),
            Users = new RecordingUsersStore(),
            Tokens = new RecordingTokenService(),
            Logs = new CapturingLoggerProvider(),
        };

        public static AuthorityFixture For(UnavailableCase scenario)
        {
            var fixture = Valid();
            switch (scenario)
            {
                case UnavailableCase.UserManagementFlagOff:
                    fixture = fixture.WithUserManagementEnabled(false);
                    break;
                case UnavailableCase.UserManagementStatusFault:
                    fixture.UserManagement.FindException =
                        new UserManagementCallException("phone/find-or-create", 503);
                    break;
                case UnavailableCase.UserManagementDependencyTimeout:
                    fixture.UserManagement.FindException = new TaskCanceledException("dependency timeout");
                    break;
                case UnavailableCase.EmptyIdentity:
                    fixture.UserManagement.Identity = fixture.UserManagement.Identity with { UserId = string.Empty };
                    break;
                case UnavailableCase.NonCanonicalIdentity:
                    fixture.UserManagement.Identity = fixture.UserManagement.Identity with { UserId = "not-a-canonical-id" };
                    break;
                case UnavailableCase.MissingRoles:
                    fixture.UserManagement.Roles = null;
                    break;
                case UnavailableCase.RoleStatusFault:
                    fixture.UserManagement.RoleException =
                        new UserManagementCallException("roles/read", 502);
                    break;
                case UnavailableCase.MismatchedRoleIdentity:
                    fixture.UserManagement.Roles = fixture.UserManagement.Roles! with { UserId = OtherUserId };
                    break;
                case UnavailableCase.EmptyRoleIdentity:
                    fixture.UserManagement.Roles = fixture.UserManagement.Roles! with { UserId = string.Empty };
                    break;
                case UnavailableCase.EmptyRoles:
                    fixture.UserManagement.Roles = fixture.UserManagement.Roles! with
                    {
                        AvailableRoles = Array.Empty<string>(),
                    };
                    break;
                case UnavailableCase.MalformedRole:
                    fixture.UserManagement.Roles = fixture.UserManagement.Roles! with
                    {
                        AvailableRoles = new[] { Roles.Client, " driver " },
                        ActiveRole = Roles.Client,
                    };
                    break;
                case UnavailableCase.ActiveRoleNotHeld:
                    fixture.UserManagement.Roles = fixture.UserManagement.Roles! with
                    {
                        AvailableRoles = new[] { Roles.Client },
                        ActiveRole = Roles.Jeeber,
                    };
                    break;
                case UnavailableCase.ModerationUncertain:
                    fixture.Suspensions.Exception = new HttpRequestException("authority unavailable");
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null);
            }

            return fixture;
        }

        private AuthorityFixture WithUserManagementEnabled(bool enabled) => new()
        {
            UserManagementEnabled = enabled,
            Otp = Otp,
            UserManagement = UserManagement,
            Suspensions = Suspensions,
            Users = Users,
            Tokens = Tokens,
            Logs = Logs,
        };
    }

    private sealed class ControlledUserManagementClient : IUserManagementDualRoleClient
    {
        public int FindCalls { get; private set; }
        public int RoleCalls { get; private set; }
        public Exception? FindException { get; set; }
        public Exception? RoleException { get; set; }
        public PhoneFindOrCreateResult Identity { get; set; } = new(
            UserId,
            IsNew: false,
            AvailableRoles: new[] { JeebGateway.Users.Roles.Client },
            ActiveRole: JeebGateway.Users.Roles.Client);
        public UserRolesResult? Roles { get; set; } = new(
            UserId,
            new[] { JeebGateway.Users.Roles.Client, JeebGateway.Users.Roles.Jeeber },
            JeebGateway.Users.Roles.Client);

        public Task<PhoneFindOrCreateResult> PhoneFindOrCreateAsync(string phone, CancellationToken ct)
        {
            FindCalls++;
            if (FindException is not null) throw FindException;
            return Task.FromResult(Identity);
        }

        public Task<UserRolesResult?> GetUserRolesAsync(string userId, CancellationToken ct)
        {
            RoleCalls++;
            if (RoleException is not null) throw RoleException;
            return Task.FromResult(Roles);
        }

        public Task<RoleSwitchReissueResult> RoleSwitchAsync(
            string userId, string opaqueRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleGrantResult> AppendAvailableRoleAsync(
            string userId, string opaqueRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<RoleGrantResult> RemoveAvailableRoleAsync(
            string userId, string opaqueRole, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class ControlledSuspensionSource : IUserSuspensionSource
    {
        public int Calls { get; private set; }
        public Exception? Exception { get; set; }
        public bool CancelFromCaller { get; set; }
        public UserSuspension Result { get; set; } = UserSuspension.None;

        public Task<UserSuspension> ReadAsync(string userId, CancellationToken ct)
        {
            Calls++;
            if (CancelFromCaller) ct.ThrowIfCancellationRequested();
            if (Exception is not null) throw Exception;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingOtpClient : IServiceOTPClient
    {
        public int SendCalls { get; private set; }
        public int ValidateCalls { get; private set; }

        public Task SendOTPAsync(SendOTPRequestUserID? body) =>
            SendOTPAsync(body, CancellationToken.None);
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.CompletedTask;
        }
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) =>
            ValidateOTPAsync(body, CancellationToken.None);
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            return Task.CompletedTask;
        }
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingUsersStore : IUsersStore
    {
        public int GetOrCreateCalls { get; private set; }
        public int ProjectionWrites { get; private set; }
        public int OtherMutationCalls { get; private set; }

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct) =>
            Task.FromResult<UserProfile?>(null);
        public Task<UserProfile?> GetForModerationAsync(string userId, CancellationToken ct) =>
            Task.FromResult<UserProfile?>(null);
        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
        {
            GetOrCreateCalls++;
            return Task.FromResult(new UserProfile
            {
                Id = userId,
                Phone = userId,
                Name = string.Empty,
                Roles = new List<string> { Roles.Client },
                ActiveRole = Roles.Client,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
        {
            ProjectionWrites++;
            return Task.CompletedTask;
        }
        public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SavedAddress>>(Array.Empty<SavedAddress>());
        public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct) =>
            Task.FromResult<SavedAddress?>(null);
        public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<SavedAddress?> UpdateAddressAsync(
            string userId, string addressId, AddressUpsert patch, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct) =>
            Task.FromResult(new UserSearchResult
            {
                Items = Array.Empty<UserProfile>(),
                Total = 0,
            });
        public Task<UserProfile?> SuspendAsync(
            string userId, string reason, string adminId, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserProfile?> RevokeRoleAsync(string userId, string role, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
        {
            OtherMutationCalls++;
            throw new NotSupportedException();
        }
        public Task<UserRoleCounts> CountByRolesAsync(
            IReadOnlyCollection<string> opaqueRoles, CancellationToken ct) =>
            Task.FromResult(UserRoleCounts.Empty);
    }

    private sealed class RecordingTokenService : ITokenService
    {
        public int IssueCalls { get; private set; }
        public string? LastUserId { get; private set; }
        public IReadOnlyList<string> LastRoles { get; private set; } = Array.Empty<string>();

        public Task<TokenPair> IssueAsync(
            string userId, IEnumerable<string> roles, CancellationToken ct)
        {
            IssueCalls++;
            LastUserId = userId;
            LastRoles = roles.ToArray();
            return Task.FromResult(new TokenPair
            {
                AccessToken = "access-test-token",
                RefreshToken = "refresh-test-token",
                AccessTokenExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15),
                RefreshTokenExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
        }

        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeAsync(
            string refreshToken, RevocationReason reason, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<int> RevokeAllForUserAsync(
            string userId, RevocationReason reason, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _records = new();

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_records)
                {
                    return _records.Select(record => record.Message).ToArray();
                }
            }
        }

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);
        public void Dispose() { }

        private sealed class CapturingLogger(
            CapturingLoggerProvider owner,
            string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._records)
                {
                    owner._records.Add((category, formatter(state, exception)));
                }
            }
        }
    }
}
