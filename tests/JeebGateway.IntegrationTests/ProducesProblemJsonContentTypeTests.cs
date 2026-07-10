using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.IntegrationTests.Infrastructure;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-261 regression lock — the class-level <c>[Produces(...)]</c> filter on four
/// OTP / auth / user controllers CLEARED each <see cref="Microsoft.AspNetCore.Mvc.ObjectResult"/>'s
/// own <c>ContentTypes</c> and forced the FIRST listed media type, downgrading the RFC 7807
/// error bodies these surfaces emit (via <see cref="OtpSignInProblems"/> or
/// <c>ControllerBase.Problem</c>, both <c>application/problem+json</c>) to
/// <c>application/json</c>. This is the exact same defect+fix PR #242 applied to
/// <c>AuthRefreshV1Controller</c> — mirrored here for its four siblings:
///
/// <list type="bullet">
///   <item><c>AuthEmailFacadeController</c>  — <c>[Route("v1/auth")]</c> (was single-arg <c>[Produces("application/json")]</c>, the worst offender)</item>
///   <item><c>AuthOtpController</c>          — <c>[Route("v1/auth/otp")]</c></item>
///   <item><c>OtpController</c>              — <c>[Route("api/otp")]</c></item>
///   <item><c>UsersMeController</c>          — <c>[Route("v1/users/me")]</c></item>
/// </list>
///
/// Each test drives ONE controller down a deterministic error path that returns a
/// ProblemDetails <c>ObjectResult</c> from the CONTROLLER BODY (not a middleware/auth
/// short-circuit — the <c>UsersMe</c> case mints a real capable bearer so it clears
/// <c>[Authorize]</c>+<c>[RequireCapability]</c> and genuinely reaches the action), then
/// asserts the RESPONSE Content-Type is <c>application/problem+json</c>. With the
/// <c>[Produces]</c> attribute present these assertions FAIL (header is
/// <c>application/json</c>); with it removed they PASS. They therefore fail if the
/// attribute is ever reintroduced.
///
/// All hosts are in-memory <see cref="WebApplicationFactory{TEntryPoint}"/> with stubbed
/// downstream clients — NO Docker / Testcontainers.
/// </summary>
public class ProducesProblemJsonContentTypeTests
{
    private const string ProblemJson = "application/problem+json";

    // -----------------------------------------------------------------------------
    // AuthEmailFacadeController [Route("v1/auth")] — was [Produces("application/json")]
    // Empty body -> the in-action bad_request 400 guard (OtpSignInProblems.Problem).
    // -----------------------------------------------------------------------------
    [Fact]
    public async Task AuthEmailFacade_Login_MissingCredentials_Returns_ProblemJson_ContentType()
    {
        using var factory = new WebApplicationFactory<Program>();
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/login",
            new StringContent("""{ }""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson,
            "the RFC 7807 error body must NOT be downgraded to application/json by a class-level [Produces] (JEBV4-261)");
        (await resp.Content.ReadAsStringAsync()).Should().Contain("bad_request",
            "the body is the frozen bad_request problem, not a bare string");
    }

    // -----------------------------------------------------------------------------
    // AuthOtpController [Route("v1/auth/otp")]
    // Otp upstream OFF -> RequestOtp returns UpstreamDisabled() 503 (OtpSignInProblems.Problem).
    // -----------------------------------------------------------------------------
    [Fact]
    public async Task AuthOtp_Request_FlagOff_Returns_ProblemJson_ContentType()
    {
        using var factory = MakeOtpFactory(otpEnabled: false);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            new StringContent("""{ "phone": "+9613000901" }""", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resp.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson,
            "the 503 fail-closed body is RFC 7807 and must keep its problem+json media type (JEBV4-261)");
    }

    // -----------------------------------------------------------------------------
    // OtpController [Route("api/otp")]
    // Otp upstream OFF -> Send returns UpstreamDisabled() 503. This surface uses the
    // built-in ControllerBase.Problem() (AddProblemDetails is wired in Program.cs), so
    // this test also empirically proves problem+json survives once [Produces] is gone.
    // -----------------------------------------------------------------------------
    [Fact]
    public async Task Otp_Send_FlagOff_Returns_ProblemJson_ContentType()
    {
        using var factory = MakeOtpFactory(otpEnabled: false);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/api/otp/send",
            new StringContent("""{ "phoneNumber": "+9613000902", "applicationId": "app-1" }""",
                Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resp.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson,
            "ControllerBase.Problem + AddProblemDetails yields problem+json once [Produces] no longer clobbers it (JEBV4-261)");
    }

    // -----------------------------------------------------------------------------
    // UsersMeController [Route("v1/users/me")]  ([Authorize] + [RequireCapability])
    // UserManagement upstream OFF -> GetMe returns UpstreamDisabled() 503 from the body
    // (OtpSignInProblems.UsersProblem). A REAL gateway-session bearer (aud=jeeb-clients,
    // role=client => ProfileReadSelf = AnyAuthenticated) is required so the request clears
    // [Authorize] + [RequireCapability] and actually reaches the action body — otherwise
    // the 401/403 would be produced by middleware, not the controller (a vacuous test).
    // -----------------------------------------------------------------------------
    [Fact]
    public async Task UsersMe_Get_FlagOff_Returns_ProblemJson_ContentType()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.Configure<UpstreamFeatureFlags>(f => f.UserManagement = false)));
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", CapabilityTestHarness.MintBearer(factory, "client"));

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        resp.Content.Headers.ContentType?.MediaType.Should().Be(ProblemJson,
            "the 503 fail-closed body returned from the controller body is RFC 7807 (JEBV4-261)");
    }

    // ----------------------------------------------------------------------- helpers

    private static WebApplicationFactory<Program> MakeOtpFactory(bool otpEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new NoopOtpClient());
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = otpEnabled);
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = "jeeb-test-app";
                    o.TtlSeconds = 300;
                });
            });
        });

    /// <summary>A no-op OTP client — the flag-off paths never dial it; it only needs to satisfy DI.</summary>
    private sealed class NoopOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
