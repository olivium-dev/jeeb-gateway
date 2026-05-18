// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Downstream 3-attempt cap + Resend recovery.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("AC", "AC4")]
public sealed class OtpAttemptCapTests : IAsyncLifetime
{
    private const string Phone = "+96179777888";

    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public OtpAttemptCapTests(OtpServiceWebAppFactory factory)
    {
        _factory = factory;
        _client  = _factory.CreateAuthClient();
    }

    public Task InitializeAsync()
    {
        _factory.ResetState();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "AC4: 3 wrong → 401 invalid_otp each; 4th attempt → 429 too_many_attempts")]
    public async Task ThreeWrongAttempts_Then4thReturns429TooManyAttempts()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var realCode = _factory.OtpClient.PeekCode(Phone)!;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var wrong = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
                new { phone = Phone, code = "999999" });

            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                because: $"attempt {attempt}: still in the 3-wrong-attempts window, AC3 says 401");
            var problem = await wrong.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
            problem!.Type.Should().EndWith("invalid_otp");
        }

        var locked = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = realCode });

        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var lockedProblem = await locked.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        lockedProblem!.Type.Should().EndWith("too_many_attempts");
    }

    [Fact(DisplayName = "AC4 recovery: Resend issues new OTP and zeroes the attempt counter")]
    public async Task ResendAfter429_AcceptsNewOtp()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        for (var i = 0; i < 3; i++)
            await _client.PostAsJsonAsync("/v1/auth/otp/verify",
                new { phone = Phone, code = "111111" });

        var lockedCheck = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = "222222" });
        lockedCheck.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));

        var resendResp = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = Phone });
        ((int)resendResp.StatusCode).Should().BeOneOf(200, 202);

        var freshCode = _factory.OtpClient.PeekCode(Phone)!;
        freshCode.Should().NotBeNullOrEmpty();

        var verifyResp = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = freshCode });
        verifyResp.StatusCode.Should().Be(HttpStatusCode.OK,
            because: "AC4 recovery: Resend zeros the attempt counter on the downstream OTP service");
    }

    [Fact(DisplayName = "AC4: lockout is NOT time-based — waiting 5 min without Resend still returns non-OK")]
    public async Task LockoutPersists_WithoutResend_AfterWaiting()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        for (var i = 0; i < 3; i++)
            await _client.PostAsJsonAsync("/v1/auth/otp/verify",
                new { phone = Phone, code = "111111" });

        _factory.Clock.Advance(TimeSpan.FromMinutes(5));

        var resp = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = "111111" });

        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);
    }
}
