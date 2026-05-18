// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — PR #32 review blockers regression tests.
//
// Covers:
//   B1 — Phone hash is deterministic (HMAC-SHA256 + pepper), not bcrypt-random.
//   B3 — SigningKey starting with "dev-only-" is rejected in non-Development.
//   S3 — /v1/auth/refresh returns invalid_refresh_token (not invalid_otp).
//
// B2 (UseForwardedHeaders + Redis warning) is observable only at runtime
// behind a proxy; the wiring in Program.cs is covered by code review.
// S1 (service_unavailable frozen-set membership) is covered structurally by
// the existing CrossStoryMobileContractTests.EveryReturnedType_IsInTheFrozenSet
// which now reads OtpProblemTypes.FrozenSet directly.

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("PR", "32")]
public sealed class PrReviewBlockerTests : IAsyncLifetime
{
    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;

    public PrReviewBlockerTests(OtpServiceWebAppFactory factory)
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

    // -----------------------------------------------------------------------
    // S3 — /v1/auth/refresh returns invalid_refresh_token, NOT invalid_otp.
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "S3: /refresh with missing body returns invalid_refresh_token type")]
    [Trait("Blocker", "S3")]
    public async Task Refresh_MissingToken_Returns_InvalidRefreshToken()
    {
        var resp = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var problem = await resp.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_refresh_token",
            because: "S3: mobile must distinguish 'wrong code' (invalid_otp) from 'session expired' (invalid_refresh_token).");
        problem.Type.Should().NotEndWith("invalid_otp");
    }

    [Fact(DisplayName = "S3: /refresh with garbage token returns invalid_refresh_token type")]
    [Trait("Blocker", "S3")]
    public async Task Refresh_GarbageToken_Returns_InvalidRefreshToken()
    {
        var resp = await _client.PostAsJsonAsync("/v1/auth/refresh",
            new { refreshToken = "not-a-jwt" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var problem = await resp.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_refresh_token");
    }

    [Fact(DisplayName = "S3: replayed (revoked-family) refresh token still returns invalid_refresh_token type")]
    [Trait("Blocker", "S3")]
    public async Task Refresh_ReplayedToken_Returns_InvalidRefreshToken()
    {
        const string phone = "+96179505050";
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone });
        var code = _factory.OtpClient.PeekCode(phone)!;
        var signin = await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code });
        signin.EnsureSuccessStatusCode();
        var pair = await signin.Content.ReadFromJsonAsync<TokenPair>();

        // Rotate once, then replay the now-revoked token.
        await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = pair!.RefreshToken });
        var replay = await _client.PostAsJsonAsync("/v1/auth/refresh", new { refreshToken = pair.RefreshToken });

        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await replay.Content.ReadFromJsonAsync<OtpRequestTests.ProblemDetailsBody>();
        problem!.Type.Should().EndWith("invalid_refresh_token");
    }

    // -----------------------------------------------------------------------
    // B1 — Phone hash is deterministic via HMAC-SHA256 + pepper.
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "B1: HmacShaPhoneHasher produces identical output for identical input across instances")]
    [Trait("Blocker", "B1")]
    public void HmacPhoneHasher_DeterministicAcrossInstances()
    {
        var options = Options.Create(new JeebJwtOptions
        {
            PhonePepper = "deterministic-test-pepper-must-be-at-least-thirty-two-bytes",
        });

        using var h1 = new HmacShaPhoneHasher(options);
        using var h2 = new HmacShaPhoneHasher(options);

        h1.HashE164("+96179123456").Should().Be(h2.HashE164("+96179123456"));
        h1.HashE164("+96179123456").Should().NotBe(h1.HashE164("+96179999999"),
            because: "different phones must produce different hashes (collision resistance).");
    }

    [Fact(DisplayName = "B1: HmacShaPhoneHasher rejects empty pepper at construction (env / sealed-secret discipline)")]
    [Trait("Blocker", "B1")]
    public void HmacPhoneHasher_RejectsEmptyPepper()
    {
        var options = Options.Create(new JeebJwtOptions { PhonePepper = "" });

        var act = () => new HmacShaPhoneHasher(options);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*PhonePepper*");
    }

    // -----------------------------------------------------------------------
    // B3 — dev-only signing key is refused outside Development.
    // -----------------------------------------------------------------------
    [Fact(DisplayName = "B3: Production env + 'dev-only-' SigningKey → host startup throws")]
    [Trait("Blocker", "B3")]
    public async Task Production_DevOnlySigningKey_FailsStart()
    {
        var act = async () => await StartHostFor(
            environmentName: Environments.Production,
            signingKey:      "dev-only-this-key-is-exactly-the-shape-the-dev-config-file-uses-AAA");

        await act.Should().ThrowAsync<OptionsValidationException>()
                 .WithMessage($"*{OtpSignInServiceCollectionExtensions.DevOnlySigningKeyPrefix}*");
    }

    [Fact(DisplayName = "B3: Staging env + 'dev-only-' SigningKey → host startup throws")]
    [Trait("Blocker", "B3")]
    public async Task Staging_DevOnlySigningKey_FailsStart()
    {
        var act = async () => await StartHostFor(
            environmentName: Environments.Staging,
            signingKey:      "dev-only-this-key-is-exactly-the-shape-the-dev-config-file-uses-AAA");

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact(DisplayName = "B3: Development env + 'dev-only-' SigningKey → host starts (dev-only key is allowed in dev)")]
    [Trait("Blocker", "B3")]
    public async Task Development_DevOnlySigningKey_StartsSuccessfully()
    {
        using var host = await StartHostFor(
            environmentName: Environments.Development,
            signingKey:      "dev-only-this-key-is-exactly-the-shape-the-dev-config-file-uses-AAA");

        host.Services.GetRequiredService<IOptions<JeebJwtOptions>>().Value.SigningKey
            .Should().StartWith("dev-only-");

        await host.StopAsync();
    }

    [Fact(DisplayName = "B3: Production env + non-dev SigningKey → host starts")]
    [Trait("Blocker", "B3")]
    public async Task Production_RealSigningKey_StartsSuccessfully()
    {
        using var host = await StartHostFor(
            environmentName: Environments.Production,
            signingKey:      "prod-real-signing-key-must-be-at-least-sixty-four-bytes-for-HS512-pad");

        host.Services.GetRequiredService<IOptions<JeebJwtOptions>>().Value.SigningKey
            .Should().NotStartWith("dev-only-");

        await host.StopAsync();
    }

    private static async Task<IHost> StartHostFor(string environmentName, string signingKey)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = environmentName,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["JeebJwt:SigningKey"]      = signingKey,
            ["JeebJwt:Issuer"]          = "https://test.auth.jeeb",
            ["JeebJwt:Audience"]        = "jeeb-mobile",
            ["JeebJwt:AccessTtlSeconds"]  = "3600",
            ["JeebJwt:RefreshTtlSeconds"] = "2592000",
            ["JeebJwt:PhonePepper"]     = "test-pepper-must-be-at-least-thirty-two-bytes-padding-AAA",
            ["GatewayRateLimit:PerPhonePerMin"] = "3",
            ["GatewayRateLimit:PerIpPerMin"]    = "10",
            ["UserManagementApi:BaseUrl"] = "http://fake-user-mgmt",
            ["ServiceOTPApi:BaseUrl"]     = "http://fake-otp",
        });

        // The OTP-signin DI needs TimeProvider; production registers it from
        // Program.cs but a standalone Host.CreateApplicationBuilder does not.
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddJeebOtpSignIn(builder.Configuration, builder.Environment);

        var host = builder.Build();
        // ValidateOnStart's IStartupValidator runs as a hosted service on
        // StartAsync; that's where the dev-only-prefix guard fires.
        await host.StartAsync();
        return host;
    }

    private sealed record TokenPair(string AccessToken, string RefreshToken);
}
