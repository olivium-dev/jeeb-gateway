using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S02 F-D (JEB-37 / JEB-1430) — <c>POST /v1/auth/refresh</c> family rotation.
/// The rotation/reuse logic is the EXISTING <c>ITokenService.RefreshAsync</c>
/// (rotate-on-use + reuse-detection -> whole-family revoke); these tests prove
/// the new v1 route surfaces it correctly end-to-end through the real host:
///
///   H-A4  rotate          : a valid refresh -> 200 with a NEW pair; the new
///                           accessToken/refreshToken differ from the presented one.
///   N8    reuse/replay     : replaying the now-rotated (old) refresh -> 401, and
///                           the whole family is revoked so the previously-issued
///                           NEW refresh also -> 401 (forces re-OTP).
///   guards: missing token -> 400; bogus token -> 401.
///
/// Also covers <c>POST /v1/auth/logout</c> (JEBV4-244 regression — the route
/// previously 404'd in production with no test catching it): valid token ->
/// 204 and the token is actually revoked; unknown/garbage token -> 204
/// (idempotent, no oracle); missing token -> 400 problem+json. Logout lives
/// on the same <see cref="AuthRefreshV1Controller"/> as refresh, so it shares
/// this file and its <see cref="MakeFactory"/> / <see cref="MintSession"/> harness.
///
/// The refresh tokens are minted by the REAL verify path (the genuine
/// IUsersStore + ITokenService singletons in the test host), so nothing is faked.
/// </summary>
public class AuthRefreshV1Tests
{
    private const string AppId = "jeeb-test-app";

    [Fact]
    public async Task HA4_ValidRefresh_Rotates_To_NewPair()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var first = await MintSession(http, "+9613000201");

        var resp = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = first.RefreshToken });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await resp.Content.ReadFromJsonAsync<RefreshDto>();
        rotated.Should().NotBeNull();
        rotated!.AccessToken.Should().NotBeNullOrWhiteSpace();
        rotated.RefreshToken.Should().NotBeNullOrWhiteSpace();
        rotated.RefreshToken.Should().NotBe(first.RefreshToken,
            "rotate-on-use issues a NEW refresh token and revokes the presented one");
    }

    [Fact]
    public async Task N8_Replay_Of_Rotated_Token_Revokes_Whole_Family_With_401()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var first = await MintSession(http, "+9613000202");

        // Rotate once: old -> new.
        var rotateResp = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = first.RefreshToken });
        rotateResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var newPair = await rotateResp.Content.ReadFromJsonAsync<RefreshDto>();

        // Replay the OLD (now rotated) refresh -> reuse detected -> 401.
        var replay = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = first.RefreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "replay of a rotated refresh token must be rejected");
        (await replay.Content.ReadAsStringAsync()).Should().Contain("invalid_refresh");

        // The whole family is now revoked: the NEW refresh also -> 401, forcing re-OTP.
        var newAlsoDead = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = newPair!.RefreshToken });
        newAlsoDead.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "reuse detection burns the ENTIRE family — the live refresh is now dead too");
    }

    [Fact]
    public async Task MissingRefreshToken_Returns400()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/refresh",
            new StringContent("""{ }""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_request");
    }

    [Fact]
    public async Task BogusRefreshToken_Returns401()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = "definitely-not-a-real-refresh-token" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_refresh");
    }

    // ---------------------------------------------------------------
    // POST /v1/auth/logout (JEBV4-244) — the route literally 404'd in
    // production because no test exercised it; these pin the v1 route.
    // ---------------------------------------------------------------

    [Fact]
    public async Task Logout_ValidRefreshToken_Returns204_AndRevokesSession()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var session = await MintSession(http, "+9613000203");

        var logout = await http.PostAsJsonAsync("/v1/auth/logout",
            new { refreshToken = session.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Prove revocation actually happened THROUGH THE V1 ROUTE: the same
        // token can no longer be redeemed for a fresh pair. Before JEBV4-244
        // was fixed, /v1/auth/logout 404'd, so this call would have gone
        // nowhere and the token would still be live.
        var refreshAfterLogout = await http.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = session.RefreshToken });
        refreshAfterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "logout must revoke the refresh token so it can no longer be redeemed");
    }

    [Fact]
    public async Task Logout_UnknownRefreshToken_Returns204_Idempotent()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/logout",
            new { refreshToken = "definitely-not-a-real-refresh-token" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "logout is idempotent — an unknown or already-revoked token still " +
            "no-ops to 204, never disclosing whether the token existed (no enumeration oracle)");
    }

    [Fact]
    public async Task Logout_MissingRefreshToken_Returns400_ProblemJson()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/logout",
            new StringContent("""{ }""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_request");
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    /// <summary>Mints a real session via the verify path and returns its pair.</summary>
    private static async Task<RefreshDto> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the verify path mints a real session");
        var body = await resp.Content.ReadFromJsonAsync<VerifyDto>();
        body.Should().NotBeNull();
        body!.RefreshToken.Should().NotBeNullOrWhiteSpace();
        return new RefreshDto { AccessToken = body.AccessToken, RefreshToken = body.RefreshToken };
    }

    private static WebApplicationFactory<Program> MakeFactory(IServiceOTPClient stub) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = true);
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private sealed class VerifyDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class RefreshDto
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
    }

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
