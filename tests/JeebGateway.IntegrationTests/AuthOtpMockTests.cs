using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests for the ADDITIVE, ENV-GATED [DevOnly] OTP mock that restores
/// <c>POST /v1/auth/otp/{request,verify}</c> at the OLD contract and backs the
/// deprecated <c>/api/otp/{send,validate}</c> aliases when
/// <c>Features:DevEndpoints:Enabled</c> is on
/// (<see cref="JeebGateway.Auth.OtpDevMock.AuthOtpMockController"/> /
/// <see cref="JeebGateway.Auth.OtpDevMock.DevOtpMock"/>).
///
/// Contracts asserted:
///   * <b>flag-off → 404</b> on the restored <c>/v1/auth/otp/*</c> routes (the
///     <see cref="JeebGateway.Security.DevOnlyAttribute"/> gate) — production sees
///     no behaviour change.
///   * <b>request</b> → 200 with a deterministic <c>ttlSeconds</c> (no upstream).
///   * <b>verify-correct-code</b> → 200 with a REAL minted session
///     (<c>accessToken</c> + <c>refreshToken</c> + <c>user</c>).
///   * <b>verify-WRONG-code → 401 invalid_otp</b> (the anti-gaming NEGATIVE: a
///     mock that auto-passes is a FALSE pass).
///   * <b>attempt cap</b> → the 3rd wrong code locks the OTP (429
///     too_many_attempts) and a subsequently-CORRECT code still fails while
///     locked, until a fresh request clears it.
///   * <b>aliases</b> <c>/api/otp/send|validate</c> map to the same mock when the
///     dev flag is on.
///
/// No upstream <c>one-time-password</c>, no Twilio, no SMS — the mock is
/// credential-free; <c>ITokenService</c> + <c>IUsersStore</c> are the real
/// in-memory singletons, so verify mints a genuine gateway JWT pair.
/// </summary>
public class AuthOtpMockTests
{
    private const string FixedCode = "123456";   // OTP_LOGIN_CODE
    private const string WrongCode = "000000";   // OTP_WRONG_CODE

    // -----------------------------------------------------------------
    // flag OFF -> 404 on the restored /v1/auth/otp/* routes
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("/v1/auth/otp/request")]
    [InlineData("/v1/auth/otp/verify")]
    public async Task RestoredRoutes_FlagOff_Return404(string path)
    {
        using var factory = NewFactory(enabled: false);
        var client = factory.CreateClient();

        var resp = await client.PostAsync(path, JsonBody("""
            { "phone": "+9613000001", "code": "123456" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "the restored OTP routes must behave as if they do not exist while the dev flag is off");
    }

    // -----------------------------------------------------------------
    // request -> 200 + deterministic ttlSeconds
    // -----------------------------------------------------------------

    [Fact]
    public async Task Request_FlagOn_Returns200_WithTtlSeconds()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/v1/auth/otp/request", JsonBody("""
            { "phone": "+9613000001" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpRequestResponseDto>();
        body.Should().NotBeNull();
        body!.TtlSeconds.Should().BeGreaterThan(0, "the mock surfaces a deterministic ttl");
        body.TtlSeconds.Should().Be(300);
    }

    // -----------------------------------------------------------------
    // verify CORRECT code -> 200 + a real minted session
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verify_FlagOn_CorrectCode_Returns200_WithRealToken()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        const string phone = "+9613000002";

        (await client.PostAsync("/v1/auth/otp/request", JsonBody($$"""{ "phone": "{{phone}}" }""")))
            .EnsureSuccessStatusCode();

        var resp = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{FixedCode}}" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpVerifyResponseDto>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace("verify must mint a real access token");
        body.RefreshToken.Should().NotBeNullOrWhiteSpace("verify must mint a real refresh token");
        body.User.Should().NotBeNull();
        body.User!.UserId.Should().NotBeNullOrWhiteSpace("a session must resolve to a real user id");
        body.User.AvailableRoles.Should().NotBeNull();

        // The minted access token is a real, three-segment JWT.
        body.AccessToken!.Split('.').Should().HaveCount(3, "a real signed JWT has header.payload.signature");
    }

    // -----------------------------------------------------------------
    // verify WRONG code -> 401 invalid_otp  (ANTI-GAMING NEGATIVE)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verify_FlagOn_WrongCode_Returns401_AndMintsNoToken()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        const string phone = "+9613000003";

        (await client.PostAsync("/v1/auth/otp/request", JsonBody($$"""{ "phone": "{{phone}}" }""")))
            .EnsureSuccessStatusCode();

        var resp = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{WrongCode}}" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a wrong code MUST fail — a mock that auto-passes is a false pass");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("invalid_otp", "the failure surfaces the frozen invalid_otp problem type");
        raw.ToLowerInvariant().Should().NotContain("accesstoken",
            "no token may be minted for a wrong code");
    }

    // -----------------------------------------------------------------
    // attempt cap -> 429 too_many_attempts; correct code still fails while locked
    // -----------------------------------------------------------------

    [Fact]
    public async Task Verify_FlagOn_AttemptCap_LocksOtp_ThenCorrectCodeStillFails_UntilNewRequest()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        const string phone = "+9613000004";

        (await client.PostAsync("/v1/auth/otp/request", JsonBody($$"""{ "phone": "{{phone}}" }""")))
            .EnsureSuccessStatusCode();

        // Attempts 1 and 2 (wrong) → 401 invalid_otp.
        for (var i = 0; i < 2; i++)
        {
            var r = await client.PostAsync("/v1/auth/otp/verify",
                JsonBody($$"""{ "phone": "{{phone}}", "code": "{{WrongCode}}" }"""));
            r.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"wrong attempt {i + 1} is invalid_otp");
        }

        // Attempt 3 (wrong) → cap reached → 429 too_many_attempts, OTP locked.
        var capped = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{WrongCode}}" }"""));
        capped.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "the 3rd wrong attempt hits the cap");
        (await capped.Content.ReadAsStringAsync()).Should().Contain("too_many_attempts");

        // Even the CORRECT code now fails while locked (anti-gaming: cap holds).
        var lockedButCorrect = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{FixedCode}}" }"""));
        lockedButCorrect.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "a locked OTP stays locked even for the correct code until a new request");

        // A fresh request clears the lock; the correct code now succeeds.
        (await client.PostAsync("/v1/auth/otp/request", JsonBody($$"""{ "phone": "{{phone}}" }""")))
            .EnsureSuccessStatusCode();
        var cleared = await client.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{FixedCode}}" }"""));
        cleared.StatusCode.Should().Be(HttpStatusCode.OK, "a new OTP request resets the attempt lock");
    }

    [Fact]
    public async Task Verify_FlagOn_NoPriorRequest_WrongCode_Returns401()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();

        // No request was issued for this phone → verify must not auto-succeed.
        var resp = await client.PostAsync("/v1/auth/otp/verify", JsonBody("""
            { "phone": "+9613000099", "code": "123456" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "verify must fail when no OTP was ever requested — never auto-create");
    }

    // -----------------------------------------------------------------
    // aliases /api/otp/send|validate route through the same mock when on
    // -----------------------------------------------------------------

    [Fact]
    public async Task Aliases_FlagOn_RouteThroughMock_OldContract()
    {
        using var factory = NewFactory(enabled: true);
        var client = factory.CreateClient();
        const string phone = "+9613000005";

        var send = await client.PostAsync("/api/otp/send", JsonBody($$"""{ "phone": "{{phone}}" }"""));
        send.StatusCode.Should().Be(HttpStatusCode.OK, "the /api/otp/send alias maps to the mock request handler");
        (await send.Content.ReadFromJsonAsync<OtpRequestResponseDto>())!.TtlSeconds.Should().Be(300);

        var ok = await client.PostAsync("/api/otp/validate",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{FixedCode}}" }"""));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "the /api/otp/validate alias maps to the mock verify handler");
        (await ok.Content.ReadFromJsonAsync<OtpVerifyResponseDto>())!.AccessToken
            .Should().NotBeNullOrWhiteSpace();

        // Alias wrong-code is ALSO rejected (anti-gaming holds on the alias path).
        var bad = await client.PostAsync("/api/otp/validate",
            JsonBody($$"""{ "phone": "{{phone}}", "code": "{{WrongCode}}" }"""));
        bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a wrong code through the alias must also fail");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(bool enabled)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
                builder.UseSetting("Features:DevEndpoints:Enabled", enabled ? "true" : "false"));

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class OtpRequestResponseDto
    {
        public int TtlSeconds { get; set; }
    }

    private sealed class OtpVerifyResponseDto
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public OtpVerifyUserDto? User { get; set; }
    }

    private sealed class OtpVerifyUserDto
    {
        public string? UserId { get; set; }
        public string? ActiveRole { get; set; }
        public string[]? AvailableRoles { get; set; }
    }
}
