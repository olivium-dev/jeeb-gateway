// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — Refresh-token family rotation + replay revocation.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("AC", "AC5b")]
public sealed class RefreshTokenRotationTests : IAsyncLifetime
{
    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public RefreshTokenRotationTests(OtpServiceWebAppFactory factory)
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

    private async Task<(string Access, string Refresh)> SignInAsync(string phone)
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        var code = _factory.OtpClient.PeekCode(phone)!;
        var resp = await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<VerifyResponse>();
        return (body!.AccessToken, body.RefreshToken);
    }

    [Fact(DisplayName = "AC5b: /refresh issues a new refresh token and the previous one is revoked")]
    public async Task Refresh_RotatesRefreshToken_InvalidatesPrevious()
    {
        var (_, refreshN) = await SignInAsync("+96179001001");

        var rot1 = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });
        rot1.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshN1 = (await rot1.Content.ReadFromJsonAsync<VerifyResponse>())!.RefreshToken;
        refreshN1.Should().NotBe(refreshN);

        var replay = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact(DisplayName = "AC5b: replay of revoked token revokes the WHOLE family")]
    public async Task ReplayRevokedToken_RevokesEntireFamily()
    {
        var (_, refreshN) = await SignInAsync("+96179002002");

        var rot1     = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });
        var refreshN1 = (await rot1.Content.ReadFromJsonAsync<VerifyResponse>())!.RefreshToken;

        var attack = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });
        attack.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var legit = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN1 });
        legit.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            because: "AC5b: detected reuse revokes the whole family; legit holder must re-OTP");
    }

    [Fact(DisplayName = "AC5b: access token exp is 1h ± 60s; refresh exp is 30d ± 60s")]
    public async Task TokenLifetimes_MatchPolicy()
    {
        var (access, refresh) = await SignInAsync("+96179003003");
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var accJwt  = handler.ReadJwtToken(access);
        var refJwt  = handler.ReadJwtToken(refresh);

        var now = _factory.Clock.GetUtcNow();
        accJwt.ValidTo.Should().BeCloseTo(now.AddHours(1).UtcDateTime, TimeSpan.FromSeconds(60));
        refJwt.ValidTo.Should().BeCloseTo(now.AddDays(30).UtcDateTime, TimeSpan.FromSeconds(60));
    }

    [Fact(DisplayName = "AC5b: refresh family revocation forces re-OTP — sign-in works after revocation")]
    public async Task AfterFamilyRevocation_NewOtpSignInWorks()
    {
        var (_, refreshN) = await SignInAsync("+96179004004");
        var rot1     = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });
        var refreshN1 = (await rot1.Content.ReadFromJsonAsync<VerifyResponse>())!.RefreshToken;
        await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshN });

        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var (_, refreshBrandNew) = await SignInAsync("+96179004004");
        refreshBrandNew.Should().NotBe(refreshN);
        refreshBrandNew.Should().NotBe(refreshN1);

        var fresh = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = refreshBrandNew });
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record VerifyResponse(string AccessToken, string RefreshToken);
}
