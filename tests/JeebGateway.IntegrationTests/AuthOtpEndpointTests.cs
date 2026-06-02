using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Endpoint tests for the additive phone sign-in routes (R1-OTP-SIGN-IN):
///   - POST /v1/auth/otp/request  → 202 Accepted
///   - POST /v1/auth/otp/verify   → 200 OK + TokenPairResponse
///
/// Asserts the new routes call the existing <see cref="IServiceOTPClient"/>, are
/// gated by FeatureFlags:UseUpstream:Otp, return the existing TokenPairResponse
/// shape (and a verifiable JWT pair), and shape errors as RFC 7807 ProblemDetails.
/// </summary>
public class AuthOtpEndpointTests
{
    [Fact]
    public async Task Request_When_Flag_On_Returns_202_And_Calls_Upstream()
    {
        var stub = new StubOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/otp/request", new AuthOtpRequestRequest(
            Phone: "+9613000000", ApplicationId: "signin"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Verify_When_Flag_On_Returns_200_With_Valid_TokenPair()
    {
        var stub = new StubOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new AuthOtpVerifyRequest(
            Phone: "+9613000111", OtpCode: "123456", ApplicationId: "signin"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.ValidateCalls.Should().Be(1);

        var body = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
        body.AccessTokenExpiresInSeconds.Should().BeGreaterThan(0);
        body.AccessTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        body.RefreshTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Verify_When_Upstream_401_Maps_To_401_ProblemDetails_Without_Echoing_Code()
    {
        var stub = new StubOtpClient
        {
            ValidateThrows = new ApiException(
                "unauthorized", (int)HttpStatusCode.Unauthorized, null, EmptyHeaders, null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new AuthOtpVerifyRequest(
            Phone: "+9613000222", OtpCode: "999999", ApplicationId: "signin"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(401);
        problem.Detail.Should().NotContain("999999");
    }

    [Fact]
    public async Task Request_With_Missing_Fields_Returns_400_ProblemDetails()
    {
        var stub = new StubOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/otp/request", new AuthOtpRequestRequest(
            Phone: "", ApplicationId: ""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stub.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Request_When_Flag_Off_Returns_503_Without_Calling_Upstream()
    {
        var stub = new StubOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: false);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/v1/auth/otp/request", new AuthOtpRequestRequest(
            Phone: "+9613000333", ApplicationId: "signin"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stub.SendCalls.Should().Be(0);
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub, bool otpEnabled) =>
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
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = otpEnabled);
            });
        });

    private sealed class StubOtpClient : IServiceOTPClient
    {
        public int SendCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public ApiException? SendThrows { get; init; }
        public ApiException? ValidateThrows { get; init; }

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            if (SendThrows is not null) throw SendThrows;
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
