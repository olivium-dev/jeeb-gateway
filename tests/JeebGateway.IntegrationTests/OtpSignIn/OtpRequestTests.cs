// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — POST /v1/auth/otp/request happy path + boundary.
// Ported from updated-requirements/qa-scaffolding/JEB-467/
//   auth-service/AuthService.Tests/Otp/OtpRequestTests.cs
// Port adjustments: namespace + fixture using; status code remains BeOneOf(200,202).

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("AC", "AC1")]
[Trait("Story", "JEB-37")]
public sealed class OtpRequestTests : IAsyncLifetime
{
    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public OtpRequestTests(OtpServiceWebAppFactory factory)
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

    [Fact(DisplayName = "AC1: Valid LB phone → 200 with ttlSeconds:300, downstream called with purpose=login + normalized phone")]
    public async Task ValidLebanesePhone_ReturnsTtl300_AndCallsDownstreamWithLoginPurpose()
    {
        var request = new { phone = "+961 79 123 456" };
        var response = await _client.PostAsJsonAsync("/v1/auth/otp/request", request);

        ((int)response.StatusCode).Should().BeOneOf(new[] { 200, 202 },
            "AC1 specifies 200; LEAD prompt allowed 202 for async-queued variant");

        var body = await response.Content.ReadFromJsonAsync<OtpRequestResponse>();
        body.Should().NotBeNull();
        body!.TtlSeconds.Should().Be(300,
            because: "AC1 + LEAD comment 14764 lock ttlSeconds to 300 (downstream AddMinutes(5))");

        var bodyJson = await response.Content.ReadAsStringAsync();
        bodyJson.Should().NotContain("+961",     because: "AC-PhonePIIHash bans raw E.164 in success bodies");
        bodyJson.Should().NotContain("79123456", because: "AC-PhonePIIHash bans raw subscriber digits in success bodies");

        _factory.OtpClient.SendCalls.Should().ContainSingle();
        var (phone, purpose, _) = _factory.OtpClient.SendCalls[0];
        purpose.Should().Be("login");
        phone.Should().Be("+96179123456");
    }

    [Fact(DisplayName = "AC1: Response includes correlationId echo header (AC6)")]
    public async Task Response_EchoesCorrelationIdHeader()
    {
        var correlationId = "test-corr-" + Guid.NewGuid();
        _client.DefaultRequestHeaders.Remove("X-Correlation-Id");
        _client.DefaultRequestHeaders.Add("X-Correlation-Id", correlationId);

        var response = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "+96179999999" });

        response.Headers.TryGetValues("X-Correlation-Id", out var values).Should().BeTrue(
            because: "AC6: correlationId must echo on the response header");
        values!.Should().Contain(correlationId);
    }

    [Fact(DisplayName = "AC-PhoneNorm: non-LB phone → 400 Problem(type=invalid_country)")]
    [Trait("AC", "AC-PhoneNorm")]
    public async Task NonLebanesePhone_Returns400_InvalidCountryProblem()
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "+14155551234" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        problem.Should().NotBeNull();
        problem!.Type.Should().EndWith("invalid_country");

        _factory.OtpClient.SendCalls.Should().BeEmpty();
    }

    [Fact(DisplayName = "AC-PhoneNorm: garbage phone → 400 Problem(type=invalid_phone)")]
    [Trait("AC", "AC-PhoneNorm")]
    public async Task UnparseablePhone_Returns400_InvalidPhoneProblem()
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "not-a-phone" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_phone");
    }

    private sealed record OtpRequestResponse(int TtlSeconds);

    internal sealed record ProblemDetailsBody(string Type, string Title, int Status, string? Detail);
}
