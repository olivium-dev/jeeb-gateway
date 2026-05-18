// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Cross-story contract with T-MOB-004 (JEB-8).
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("CrossStory", "T-MOB-004#AC4")]
[Trait("AC", "AC4")]
public sealed class CrossStoryMobileContractTests : IAsyncLifetime
{
    private const string Phone = "+96176543210";

    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public CrossStoryMobileContractTests(OtpServiceWebAppFactory factory)
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

    [Fact(DisplayName = "T-MOB-004 AC4: 3-attempt + Resend recovery — full request sequence matches mobile state machine")]
    public async Task MobileFlow_FullSequence_BackendContractHolds()
    {
        var step1 = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "+961 76 543 210" });
        ((int)step1.StatusCode).Should().BeOneOf(200, 202);
        var step1Body = await step1.Content.ReadAsStringAsync();
        step1Body.Should().Contain("ttlSeconds");
        step1Body.Should().NotContain("+961");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var wrong = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
                new { phone = Phone, code = "999999" });
            wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            var problem = await wrong.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
            problem!.Type.Should().EndWith("invalid_otp");
        }

        var locked = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = "999999" });
        locked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var lockedProblem = await locked.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        lockedProblem!.Type.Should().EndWith("too_many_attempts");

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var resend = await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        ((int)resend.StatusCode).Should().BeOneOf(200, 202);

        var realCode = _factory.OtpClient.PeekCode(Phone)!;
        var success  = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = realCode });
        success.StatusCode.Should().Be(HttpStatusCode.OK);

        var ok = await success.Content.ReadFromJsonAsync<TokenPair>();
        ok!.AccessToken.Should().NotBeNullOrEmpty();
        ok.RefreshToken.Should().NotBeNullOrEmpty();
    }

    [Fact(DisplayName = "T-MOB-004 AC9: every ProblemDetails 'type' is in the AC-ProblemTypeSet frozen list")]
    [Trait("AC", "AC-ProblemTypeSet")]
    public async Task EveryReturnedType_IsInTheFrozenSet()
    {
        // PR #32 review S1/S3 — frozen set extended additively:
        //   + service_unavailable   (replaces ad-hoc /downstream and /user_mgmt_unavailable)
        //   + invalid_refresh_token (replaces invalid_otp on the /refresh path)
        // Mobile mapping coordinated via JEB-37 comment thread.
        var frozen = JeebGateway.Auth.OtpSignIn.OtpProblemTypes.FrozenSet;
        var seenTypes = new HashSet<string>();

        await TriggerAsync("/v1/auth/otp/verify", new { phone = "+96179111111", code = "000000" }, seenTypes);
        await TriggerAsync("/v1/auth/otp/request", new { phone = "not-a-phone" }, seenTypes);
        await TriggerAsync("/v1/auth/otp/request", new { phone = "+14155551234" }, seenTypes);

        for (var i = 0; i < 4; i++)
        {
            await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = "+96179222222" });
            _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        }

        var rateLimitedResp = await _client.PostAsJsonAsync("/v1/auth/otp/request",
            new { phone = "+96179222222" });
        var rateProblem = await rateLimitedResp.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        if (rateProblem is not null) seenTypes.Add(rateProblem.Type.Split('/').Last());

        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = "+96179333333" });
        for (var i = 0; i < 3; i++)
            await _client.PostAsJsonAsync("/v1/auth/otp/verify",
                new { phone = "+96179333333", code = "999999" });
        var tooManyResp = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = "+96179333333", code = "999999" });
        var tooManyProblem = await tooManyResp.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        if (tooManyProblem is not null) seenTypes.Add(tooManyProblem.Type.Split('/').Last());

        foreach (var t in seenTypes)
            frozen.Should().Contain(t,
                because: $"AC-ProblemTypeSet: gateway must only emit types in OtpProblemTypes.FrozenSet; saw '{t}'");
    }

    private async Task TriggerAsync(string path, object body, HashSet<string> seen)
    {
        var resp    = await _client.PostAsJsonAsync(path, body);
        var problem = await resp.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        if (problem is not null) seen.Add(problem.Type.Split('/').Last());
    }

    private sealed record TokenPair(string AccessToken, string RefreshToken);
}
