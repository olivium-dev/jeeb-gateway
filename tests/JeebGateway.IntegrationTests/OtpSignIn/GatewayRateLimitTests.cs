// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — AC-GatewayRateLimit.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("AC", "AC-GatewayRateLimit")]
public sealed class GatewayRateLimitTests : IAsyncLifetime
{
    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public GatewayRateLimitTests(OtpServiceWebAppFactory factory)
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

    [Fact(DisplayName = "Per-phone limit: 4th request within 60s for SAME phone → 429 Problem(type=rate_limited)")]
    public async Task PerPhone_FourthRequestInWindow_Returns429RateLimited()
    {
        const string phone = "+96179101010";

        for (var i = 1; i <= 3; i++)
        {
            var ok = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
            ((int)ok.StatusCode).Should().BeOneOf(new[] { 200, 202 },
                $"per-phone limit is 3/min; request {i} is still within budget");
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var limited = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var problem = await limited.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("rate_limited");

        limited.Headers.TryGetValues("Retry-After", out var retryAfter).Should().BeTrue(
            because: "RFC6585 §4: 429 responses SHOULD include a Retry-After header");
        retryAfter!.Should().NotBeEmpty();
    }

    [Fact(DisplayName = "Per-phone limit window slides: after 61s, request succeeds again")]
    public async Task PerPhone_AfterWindow_RequestSucceedsAgain()
    {
        const string phone = "+96179202020";

        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var limited = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));

        var ok = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        ((int)ok.StatusCode).Should().BeOneOf(200, 202);
    }

    [Fact(DisplayName = "Per-IP limit: 11th request from same IP for DIFFERENT phones → 429")]
    public async Task PerIp_EleventhRequestInWindow_Returns429RateLimited()
    {
        for (var i = 0; i < 10; i++)
        {
            var phone = $"+9617{i:D8}";
            var ok = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
            ((int)ok.StatusCode).Should().BeOneOf(new[] { 200, 202 },
                $"per-IP limit is 10/min; request {i + 1} is still within budget");
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var limited = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "+96179998877" });
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        var problem = await limited.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("rate_limited");
    }

    [Fact(DisplayName = "Rate-limited responses do NOT trigger downstream SendAsync")]
    public async Task RateLimitedRequest_DoesNotCallDownstream()
    {
        const string phone = "+96179303030";

        for (var i = 0; i < 3; i++)
        {
            await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        var callsBefore = _factory.OtpClient.SendCalls.Count;

        var resp = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        _factory.OtpClient.SendCalls.Count.Should().Be(callsBefore);
    }
}
