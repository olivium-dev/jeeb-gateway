using JeebGateway.Auth.OtpSignIn;
using JeebGateway.service.ServiceUserManagement;
using JeebGateway.Tokens;
using JeebGateway.Users;
using JeebGateway.Users.Moderation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class AuthEmailFacadeLoggingPrivacyTests
{
    // Synthetic markers, deliberately present in every sensitive input/response surface.
    private const string UserId = "privacy-user-marker";
    private const string Email = "privacy-email@example.invalid";
    private const string Password = "privacy-password-marker";
    private const string Token = "privacy-token-marker";
    private const string Body = "privacy-body-marker";
    private const string Sensitive = UserId + " " + Email + " " + Password + " " + Token + " " + Body;

    public static IEnumerable<object[]> MintCases =>
        from route in new[] { "login", "signup", "set-password", "social" }
        from outcome in new[] { "success", "roles-failed", "suspended", "moderation-failed" }
        select new object[] { route, outcome };

    [Theory]
    [MemberData(nameof(MintCases))]
    public async Task SessionRoutes_LogOnlyFixedEvents_WithoutIdentityCredentialsOrExceptions(string route, string outcome)
    {
        using var http = new HttpClient(new RejectNetworkHandler());
        var um = new StubUmClient(http);
        var log = new CaptureLogger();
        var tokens = Substitute.For<ITokenService>();
        tokens.IssueAsync(UserId, Arg.Any<IEnumerable<string>>(), Arg.Any<string>(),
                Arg.Any<VerifiedAuthenticationContext?>(), Arg.Any<CancellationToken>())
            .Returns(new TokenPair
            {
                AccessToken = Token, RefreshToken = Token,
                AccessTokenExpiresAt = DateTimeOffset.UnixEpoch, RefreshTokenExpiresAt = DateTimeOffset.UnixEpoch,
            });
        var roles = Substitute.For<IUserManagementDualRoleClient>();
        roles.GetUserRolesAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => outcome == "roles-failed"
            ? Task.FromException<UserRolesResult?>(new InvalidOperationException(Sensitive))
            : Task.FromResult<UserRolesResult?>(new(UserId, new[] { Roles.Client }, Roles.Client)));
        var suspensions = Substitute.For<IUserSuspensionSource>();
        suspensions.ReadAsync(UserId, Arg.Any<CancellationToken>()).Returns(_ => outcome == "moderation-failed"
            ? Task.FromException<UserSuspension>(new InvalidOperationException(Sensitive))
            : Task.FromResult(new UserSuspension(outcome == "suspended", Sensitive, Body)));
        var controller = Create(um, tokens, suspensions, roles, log);

        var result = Assert.IsAssignableFrom<ObjectResult>(await Invoke(controller, route));
        var minted = outcome is "success" or "roles-failed";
        Assert.Equal(minted ? 200 : outcome == "suspended" ? 403 : 503, result.StatusCode);
        Assert.Equal(minted ? 1 : 0, tokens.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "IssueAsync"));
        if (minted)
        {
            // Responses still carry the session; only log output is reduced.
            var json = System.Text.Json.JsonSerializer.Serialize(result.Value);
            Assert.Contains(Token, json);
            Assert.Contains(UserId, json);
        }

        var success = route == "social" ? "auth.social facade minted gateway session" : "auth.facade minted gateway session";
        var expected = outcome switch
        {
            "roles-failed" => new[] { "auth.facade UM get-roles failed; default role", success },
            "suspended" => new[] { "auth.facade refused: account suspended" },
            "moderation-failed" => new[] { "moderation lookup failed; refusing to mint a session" },
            _ => new[] { success },
        };
        Assert.Equal(expected, log.Entries.Select(e => e.Message));
        foreach (var entry in log.Entries)
        {
            Assert.Null(entry.Exception);
            var field = Assert.Single(entry.Fields);
            Assert.Equal("{OriginalFormat}", field.Key);
            Assert.Equal(entry.Message, field.Value);
        }
        Assert.Empty(log.Scopes);
    }

    [Theory]
    [InlineData("recovery")]
    [InlineData("set-password")]
    public async Task PrivateAcceptanceSuccess_WithoutSessionMint_EmitsNoLogs(string route)
    {
        using var http = new HttpClient(new RejectNetworkHandler());
        var tokens = Substitute.For<ITokenService>();
        var log = new CaptureLogger();
        var controller = Create(new StubUmClient(http), tokens,
            Substitute.For<IUserSuspensionSource>(), Substitute.For<IUserManagementDualRoleClient>(), log);

        // The private acceptance client resets without email and logs in separately.
        var action = route == "recovery"
            ? await controller.Recovery(new() { Email = Email }, default)
            : await controller.SetPassword(new() { Password = Password, ResetToken = Token }, default);
        var result = Assert.IsType<OkObjectResult>(action);
        Assert.Equal(200, result.StatusCode);
        var json = System.Text.Json.JsonSerializer.SerializeToElement(result.Value);
        if (route == "set-password") Assert.True(json.GetProperty("success").GetBoolean());
        else Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("requestId").GetString()));
        Assert.Empty(tokens.ReceivedCalls());
        Assert.Empty(log.Entries);
        Assert.Empty(log.Scopes);
    }

    [Theory]
    [InlineData("login", 401, 401)]
    [InlineData("signup", 409, 409)]
    [InlineData("set-password", 400, 400)]
    [InlineData("recovery", 404, 404)]
    [InlineData("social", 500, 502)]
    public async Task UpstreamFailure_LogsOnlyFixedRouteAndNumericStatus(string route, int upstreamStatus, int responseStatus)
    {
        using var http = new HttpClient(new RejectNetworkHandler());
        var um = new StubUmClient(http) { Failure = new ApiException(Sensitive, upstreamStatus, Sensitive,
            new Dictionary<string, IEnumerable<string>>(), new InvalidOperationException(Sensitive)) };
        var tokens = Substitute.For<ITokenService>();
        var log = new CaptureLogger();
        var controller = Create(um, tokens, Substitute.For<IUserSuspensionSource>(),
            Substitute.For<IUserManagementDualRoleClient>(), log);

        var result = Assert.IsAssignableFrom<ObjectResult>(await Invoke(controller, route));
        Assert.Equal(responseStatus, result.StatusCode);
        Assert.Empty(tokens.ReceivedCalls());
        var entry = Assert.Single(log.Entries);
        Assert.Null(entry.Exception);
        Assert.Equal($"auth.facade.{route} upstream {upstreamStatus}", entry.Message);
        Assert.Equal(3, entry.Fields.Count);
        Assert.Equal(route, entry.Fields.Single(f => f.Key == "Leg").Value);
        Assert.Equal(upstreamStatus, entry.Fields.Single(f => f.Key == "Status").Value);
        Assert.Equal("auth.facade.{Leg} upstream {Status}", entry.Fields.Single(f => f.Key == "{OriginalFormat}").Value);
        Assert.Empty(log.Scopes);
    }

    private static AuthEmailFacadeController Create(StubUmClient um, ITokenService tokens,
        IUserSuspensionSource suspensions, IUserManagementDualRoleClient roles, CaptureLogger log) =>
        new(um, tokens, Substitute.For<IUsersStore>(), suspensions, roles, new NoOpDevSeededRoleStore(), log)
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    private static Task<IActionResult> Invoke(AuthEmailFacadeController controller, string route) => route switch
    {
        "login" => controller.Login(new() { Email = Email, Password = Password }, default),
        "signup" => controller.Signup(new() { Email = Email, Password = Password, Name = Body }, default),
        "set-password" => controller.SetPassword(new() { Email = Email, Password = Password, ResetToken = Token }, default),
        "social" => controller.Social(new() { SocialId = UserId, SocialToken = Token, SocialPlatform = Body }, default),
        "recovery" => controller.Recovery(new() { Email = Email }, default),
        _ => throw new ArgumentOutOfRangeException(nameof(route)),
    };

    private sealed class StubUmClient(HttpClient http) : ServiceUserManagementClient("https://unused.invalid", http)
    {
        public ApiException? Failure { get; init; }
        private Task<T> Respond<T>(T response) => Failure is null ? Task.FromResult(response) : Task.FromException<T>(Failure);
        public override Task<LoginResponse> LoginAsync(LoginRequest? body, CancellationToken ct) =>
            Respond(new LoginResponse { UserId = UserId, AuthToken = Token, RefreshToken = Token });
        public override Task<RegisterUserResponse> RegisterAsync(RegisterUserRequest? body, CancellationToken ct) =>
            Respond(new RegisterUserResponse { UserId = UserId, Email = Email, Username = Body, Status = Body });
        public override Task<ResetPasswordResponse> ResetAsync(ResetPasswordRequest? body, CancellationToken ct) =>
            Respond(new ResetPasswordResponse { Success = true });
        public override Task<SocialLoginResponse> SocialAsync(SocialLoginRequest? body, CancellationToken ct) =>
            Respond(new SocialLoginResponse { UserId = UserId, AuthToken = Token, RefreshToken = Token, RecentlyCreated = true });
        public override Task<ForgotPasswordResponse> ForgotAsync(ForgotPasswordRequest? body, CancellationToken ct) =>
            Respond(new ForgotPasswordResponse { Success = true });
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new InvalidOperationException("Tests must not make network requests.");
    }

    private sealed record Entry(string Message, Exception? Exception, IReadOnlyList<KeyValuePair<string, object?>> Fields);
    private sealed class CaptureLogger : ILogger<AuthEmailFacadeController>
    {
        public List<Entry> Entries { get; } = new();
        public List<object> Scopes { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull { Scopes.Add(state); return null; }
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state).ToArray();
            Entries.Add(new(formatter(state, exception), exception, fields));
        }
    }
}
