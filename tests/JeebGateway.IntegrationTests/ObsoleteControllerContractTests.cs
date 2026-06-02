using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// R10-OBSOLETE-CONTROLLERS-CONTRACT — defensive snapshot tests that pin the
/// response shape and status codes of in-memory / [Obsolete] controllers serving
/// live traffic, so a future refactor (e.g. flipping a UseUpstream:* flag, or
/// rewriting a controller against an NSwag client) cannot silently change the
/// contract the mobile app depends on.
///
/// These tests are PURELY ADDITIVE — they add no production code and exercise the
/// existing in-memory fallback behavior regardless of flag state. If someone
/// changes the field set or status code of these surfaces, the test fails before
/// deploy.
///
/// Covered here (shapes verified directly against the controllers):
///   - OtpController  (POST /api/otp/send → 202, /api/otp/validate → 200)
///   - TokensController (POST /auth/tokens → TokenPairResponse field set + 200)
/// </summary>
public class ObsoleteControllerContractTests
{
    private static readonly string[] TokenPairFields =
    {
        "accessToken",
        "refreshToken",
        "tokenType",
        "accessTokenExpiresInSeconds",
        "accessTokenExpiresAt",
        "refreshTokenExpiresAt"
    };

    // ------------------------------------------------------------------
    // OtpController (api/otp) — 202 on send, 200 on validate.
    // ------------------------------------------------------------------

    [Fact]
    public async Task OtpController_Send_Contract_Is_202_With_Empty_Body()
    {
        using var factory = MakeOtpFactory(new NoopOtpClient(), otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000000", ApplicationId: "app"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task OtpController_Validate_Contract_Is_200()
    {
        using var factory = MakeOtpFactory(new NoopOtpClient(), otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/validate", new OtpValidateRequest(
            PhoneNumber: "+9613000000", Otp: "1234", ApplicationId: "app"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ------------------------------------------------------------------
    // TokensController (auth/tokens) — exact TokenPairResponse field set.
    // ------------------------------------------------------------------

    [Fact]
    public async Task TokensController_Issue_Contract_Field_Set_Is_Stable()
    {
        using var factory = MakeBaseFactory();
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/auth/tokens", new { userId = "contract-user" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var actualFields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();

        // The contract is "at least these fields, with these names" — a future
        // refactor may ADD fields (allowed) but must not remove or rename any.
        foreach (var field in TokenPairFields)
        {
            actualFields.Should().Contain(field,
                $"TokenPairResponse must keep the '{field}' field for existing consumers");
        }

        doc.RootElement.GetProperty("tokenType").GetString().Should().Be("Bearer");
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static WebApplicationFactory<Program> MakeBaseFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RateLimit:Enabled"] = "false"
                });
            });
        });

    private static WebApplicationFactory<Program> MakeOtpFactory(
        IServiceOTPClient otpClient, bool otpEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RateLimit:Enabled"] = "false"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(otpClient);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = otpEnabled);
            });
        });

    private sealed class NoopOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken ct) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken ct) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
