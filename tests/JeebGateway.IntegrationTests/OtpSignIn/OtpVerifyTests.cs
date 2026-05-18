// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — POST /v1/auth/otp/verify happy path + JWT contract.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
public sealed class OtpVerifyTests : IAsyncLifetime
{
    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public OtpVerifyTests(OtpServiceWebAppFactory factory)
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

    [Fact(DisplayName = "AC2: Correct OTP within TTL → 200 with valid JWT pair; user-mgmt record created")]
    [Trait("AC", "AC2")]
    public async Task CorrectOtp_ReturnsJwtPair_AndFindOrCreatesUser()
    {
        const string phone = "+96179111222";
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        var code = _factory.OtpClient.PeekCode("+96179111222")
            ?? throw new InvalidOperationException("Fake one-time-password did not issue a code");

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<VerifyResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();

        _factory.UserMgmtClient.FindOrCreateCalls.Should().ContainSingle();
        var (calledPhone, userId, _) = _factory.UserMgmtClient.FindOrCreateCalls[0];
        calledPhone.Should().Be("+96179111222");
        userId.Should().NotBe(Guid.Empty);

        var handler   = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var accessJwt = handler.ReadJwtToken(body.AccessToken);

        accessJwt.SignatureAlgorithm.Should().Be(SecurityAlgorithms.HmacSha512,
            because: "AC5: JWT must be signed with HS512");
        accessJwt.Issuer.Should().Be("https://test.auth.jeeb");
        accessJwt.Audiences.Should().Contain("jeeb-mobile");
        accessJwt.Subject.Should().Be(userId.ToString());

        var expectedExp = _factory.Clock.GetUtcNow().AddHours(1);
        accessJwt.ValidTo.Should().BeCloseTo(expectedExp.UtcDateTime, TimeSpan.FromSeconds(60),
            because: "AC5b: accessTtl = 1h");

        var refreshJwt   = handler.ReadJwtToken(body.RefreshToken);
        var expectedRefr = _factory.Clock.GetUtcNow().AddDays(30);
        refreshJwt.ValidTo.Should().BeCloseTo(expectedRefr.UtcDateTime, TimeSpan.FromSeconds(60),
            because: "AC5b: refreshTtl = 30d");
    }

    [Fact(DisplayName = "AC3: Wrong code → 401 Problem(type=invalid_otp)")]
    [Trait("AC", "AC3")]
    public async Task WrongOtp_Returns401_InvalidOtpProblem()
    {
        const string phone = "+96179333444";
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone, code = "000000" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await response.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_otp");

        var bodyJson = await response.Content.ReadAsStringAsync();
        bodyJson.Should().NotContain("accessToken");
        bodyJson.Should().NotContain("refreshToken");

        _factory.UserMgmtClient.FindOrCreateCalls.Should().BeEmpty();
    }

    [Fact(DisplayName = "AC-PhonePIIHash: success body MUST NOT echo raw phone")]
    [Trait("AC", "AC-PhonePIIHash")]
    public async Task SuccessResponse_DoesNotEchoRawPhone()
    {
        const string phone = "+96179555666";
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        var code = _factory.OtpClient.PeekCode(phone)!;

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code });
        var json     = await response.Content.ReadAsStringAsync();

        json.Should().NotContain("+961");
        json.Should().NotContain("79555666");
    }

    private sealed record VerifyResponse(string AccessToken, string RefreshToken, UserBlock User);
    private sealed record UserBlock(Guid UserId, string ActiveRole, string[] AvailableRoles);
}
