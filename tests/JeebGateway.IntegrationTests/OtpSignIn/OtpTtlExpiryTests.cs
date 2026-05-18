// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — OTP TTL expiry behaviour.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("AC", "AC1")]
public sealed class OtpTtlExpiryTests : IAsyncLifetime
{
    private const string Phone = "+96170123456";

    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public OtpTtlExpiryTests(OtpServiceWebAppFactory factory)
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

    [Fact(DisplayName = "OTP @ 299s still valid → 200")]
    public async Task OtpJustBeforeExpiry_IsAccepted()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var code = _factory.OtpClient.PeekCode(Phone)!;

        _factory.Clock.Advance(TimeSpan.FromSeconds(299));

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact(DisplayName = "OTP @ 301s expired → 401 Problem(type=invalid_otp)")]
    public async Task OtpAfterTtl_Returns401InvalidOtp()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var code = _factory.OtpClient.PeekCode(Phone)!;

        _factory.Clock.Advance(TimeSpan.FromSeconds(301));

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var problem = await response.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_otp");

        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("accessToken");
        json.Should().NotContain("refreshToken");
    }

    [Fact(DisplayName = "Resend after TTL expiry issues a brand-new code (non-idempotent)")]
    public async Task ResendAfterExpiry_IssuesFreshCode()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var originalCode = _factory.OtpClient.PeekCode(Phone)!;

        _factory.Clock.Advance(TimeSpan.FromSeconds(301));

        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var newCode = _factory.OtpClient.PeekCode(Phone)!;

        newCode.Should().NotBe(originalCode);
    }
}
