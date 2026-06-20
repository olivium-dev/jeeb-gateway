using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Tests G1–G7 (JEB-1516 design §5.2) for the PRODUCTION phone sign-in OTP
/// surface <see cref="AuthOtpController"/> (<c>POST /v1/auth/otp/{request,verify}</c>).
///
/// The controller is a THIN BFF: it orchestrates the shared one-time-password
/// service via the NSwag-generated <see cref="IServiceOTPClient"/> for
/// send/validate and mints the gateway session ONLY on a successful validate
/// (<c>IUsersStore.GetOrCreateAsync</c> + <c>ITokenService.IssueAsync</c>, which
/// are the real in-memory singletons in the test host, so verify mints a genuine
/// JWT pair). The retired in-gateway OTP mock — which duplicated OTP business
/// logic — is gone; the gateway now contains ZERO OTP send/validate logic.
///
/// Boundaries asserted:
///   G1  request (client→200)                 → 200 { ttlSeconds: 300 }
///   G2  verify  (client→200)                 → 200 { accessToken, refreshToken, user{...} }; real JWT; stores invoked
///   G3  verify  (client→ApiException 401)    → 401 ProblemDetails invalid_otp; upstream body NOT echoed
///   G4  verify  (client→ApiException 500)    → 502 ProblemDetails upstream-fault
///   G5  UseUpstream:Otp = false              → 503 fails-closed, no upstream call
///   G6  no OTP logic in gateway              → exactly one v1/auth/otp route owner; mock symbols absent from runtime
///   G7  contract no-drift                    → response JSON byte-shape == frozen contract
/// </summary>
public class AuthOtpControllerTests
{
    private const string AppId = "jeeb-test-app";

    // -----------------------------------------------------------------
    // G1 — request, flag on, client→200 → 200 { ttlSeconds: 300 }
    // -----------------------------------------------------------------

    [Fact]
    public async Task G1_Request_FlagOn_Returns200_WithTtlSeconds300_AndCallsUpstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000001" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OtpRequestResponseDto>();
        body.Should().NotBeNull();
        body!.TtlSeconds.Should().Be(300, "the gateway supplies the frozen contract ttl");
        stub.SendCalls.Should().Be(1, "request must orchestrate the upstream SendOTP");
        stub.LastSendApplicationId.Should().Be(AppId, "the configured Jeeb application id must be forwarded");
    }

    // -----------------------------------------------------------------
    // G2 — verify, flag on, client→200 → 200 + REAL minted session
    // -----------------------------------------------------------------

    [Fact]
    public async Task G2_Verify_FlagOn_Returns200_WithRealToken_AndInvokesStores()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000002", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.ValidateCalls.Should().Be(1, "verify must orchestrate the upstream ValidateOTP");
        stub.LastValidateApplicationId.Should().Be(AppId);

        var body = await resp.Content.ReadFromJsonAsync<OtpVerifyResponseDto>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace("verify must mint a real access token");
        body.RefreshToken.Should().NotBeNullOrWhiteSpace("verify must mint a real refresh token");
        body.User.Should().NotBeNull();
        body.User!.UserId.Should().NotBeNullOrWhiteSpace("a session resolves to a real user id (GetOrCreateAsync)");
        body.User.AvailableRoles.Should().NotBeNull();

        // The minted access token is a real, three-segment JWT (ITokenService.IssueAsync).
        body.AccessToken!.Split('.').Should().HaveCount(3, "a real signed JWT has header.payload.signature");
    }

    // -----------------------------------------------------------------
    // G3 — verify, client→ApiException(401) → 401 invalid_otp, body not echoed
    // -----------------------------------------------------------------

    [Fact]
    public async Task G3_Verify_Upstream401_Maps_To_401_InvalidOtp_WithoutEchoingBody()
    {
        var stub = new StubServiceOtpClient
        {
            ValidateThrows = new ApiException(
                "unauthorized: code 9999 rejected", (int)HttpStatusCode.Unauthorized,
                "{\"otp\":\"9999\"}", EmptyHeaders, null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000003", "code": "9999" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a wrong code MUST fail — a mock that auto-passes is a false pass");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("invalid_otp", "the failure surfaces the frozen invalid_otp problem type");
        raw.Should().Contain("https://problems.jeeb.lb/auth/invalid_otp",
            "the frozen problem-type base URI must be preserved");
        raw.Should().NotContain("9999", "the upstream body (which may embed the code) must NEVER be echoed");
        raw.ToLowerInvariant().Should().NotContain("accesstoken", "no token may be minted for a wrong code");
    }

    // -----------------------------------------------------------------
    // G4 — verify, client→ApiException(500) → 502 upstream-fault
    // -----------------------------------------------------------------

    [Fact]
    public async Task G4_Verify_Upstream500_Maps_To_502_UpstreamFault()
    {
        var stub = new StubServiceOtpClient
        {
            ValidateThrows = new ApiException(
                "boom", (int)HttpStatusCode.InternalServerError, "internal", EmptyHeaders, null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000004", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(502);
        problem.Detail.Should().NotContain("internal", "the upstream body must not be echoed");
    }

    // -----------------------------------------------------------------
    // G5 — UseUpstream:Otp = false → 503 fails-closed, no upstream call
    // -----------------------------------------------------------------

    [Fact]
    public async Task G5_FlagOff_Returns503_WithoutCallingUpstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: false);
        var http = factory.CreateClient();

        var req = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000005" }"""));
        req.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var ver = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000005", "code": "1234" }"""));
        ver.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        stub.SendCalls.Should().Be(0, "the kill switch must fail closed, never dial the upstream");
        stub.ValidateCalls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // G8 — verify, client→ApiException(429) → 429 too_many_attempts,
    //      Retry-After propagated, upstream body NOT echoed (S02 N2).
    // -----------------------------------------------------------------

    [Fact]
    public async Task G8_Verify_Upstream429_Maps_To_429_TooManyAttempts_WithRetryAfter()
    {
        var stub = new StubServiceOtpClient
        {
            ValidateThrows = new ApiException(
                "too_many_attempts: code 0000 locked", (int)HttpStatusCode.TooManyRequests,
                "{\"otp\":\"0000\",\"code\":\"too_many_attempts\"}",
                HeadersWith("Retry-After", "42"), null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000007", "code": "0000" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "an upstream verify lockout MUST propagate as a gateway 429, NOT a 502");

        resp.Headers.TryGetValues("Retry-After", out var ra).Should().BeTrue(
            "the gateway must forward the upstream back-off hint");
        ra!.Single().Should().Be("42");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("too_many_attempts", "the frozen machine code must be preserved");
        raw.Should().Contain("https://problems.jeeb.lb/auth/too_many_attempts");
        raw.Should().Contain("\"retryAfter\":42", "the back-off is mirrored as a JSON extension");
        raw.Should().NotContain("0000", "the upstream body (which may embed the code) must NEVER be echoed");
        raw.ToLowerInvariant().Should().NotContain("accesstoken", "no token may be minted on a lockout");
    }

    // -----------------------------------------------------------------
    // G9 — request, client→ApiException(429) → 429 rate_limited,
    //      Retry-After propagated (S02 N12 upstream-burst path).
    // -----------------------------------------------------------------

    [Fact]
    public async Task G9_Request_Upstream429_Maps_To_429_RateLimited_WithRetryAfter()
    {
        var stub = new StubServiceOtpClient
        {
            SendThrows = new ApiException(
                "rate_limited", (int)HttpStatusCode.TooManyRequests,
                "{\"code\":\"rate_limited\"}", HeadersWith("Retry-After", "30"), null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000008" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "an upstream request-burst throttle MUST propagate as a gateway 429, NOT a 502");

        resp.Headers.TryGetValues("Retry-After", out var ra).Should().BeTrue();
        ra!.Single().Should().Be("30");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("rate_limited", "the frozen machine code must be preserved");
        raw.Should().Contain("https://problems.jeeb.lb/auth/rate_limited");
        raw.Should().Contain("\"retryAfter\":30");
        stub.SendCalls.Should().Be(1, "the single request reaches the upstream before it throttles");
    }

    // -----------------------------------------------------------------
    // G6 — no OTP logic in gateway: exactly one v1/auth/otp route owner;
    //      the retired mock types are absent from the runtime assembly.
    // -----------------------------------------------------------------

    [Fact]
    public void G6_Gateway_Has_No_Otp_Logic_And_Single_Route_Owner()
    {
        var asm = typeof(AuthOtpController).Assembly;

        // The single owner of the v1/auth/otp route prefix is AuthOtpController.
        var owners = asm.GetTypes()
            .Where(t => typeof(ControllerBase).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), false)
                .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
                .Any(r => string.Equals(r.Template, "v1/auth/otp", StringComparison.Ordinal)))
            .ToList();

        owners.Should().ContainSingle("exactly one controller owns Route(\"v1/auth/otp\")");
        owners[0].Should().Be(typeof(AuthOtpController));

        // The retired in-gateway OTP mock types must be gone from the runtime.
        asm.GetType("JeebGateway.Auth.OtpDevMock.IDevOtpMock").Should().BeNull();
        asm.GetType("JeebGateway.Auth.OtpDevMock.DevOtpMock").Should().BeNull();
        asm.GetType("JeebGateway.Auth.OtpDevMock.AuthOtpMockController").Should().BeNull();
        asm.GetType("JeebGateway.Auth.OtpDevMock.DevOtpEndpoints").Should().BeNull();
    }

    // -----------------------------------------------------------------
    // G7 — contract no-drift: byte-shape of the frozen contract.
    // -----------------------------------------------------------------

    [Fact]
    public async Task G7_Contract_NoDrift_ResponseShapes_Match_FrozenContract()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        // request → exactly { ttlSeconds }
        var reqResp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000006" }"""));
        reqResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await reqResp.Content.ReadAsStringAsync()))
        {
            doc.RootElement.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(new[] { "ttlSeconds" });
        }

        // verify → exactly { accessToken, refreshToken, user{ userId, active_role, available_roles } }
        // S02 contract (ADR-003): the user block uses the frozen snake_case Jeeb keys
        // (matching GET /v1/users/me and the harness H-A2/H-B2 assertions).
        var verResp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000006", "code": "1234" }"""));
        verResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using (var doc = JsonDocument.Parse(await verResp.Content.ReadAsStringAsync()))
        {
            doc.RootElement.EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(new[] { "accessToken", "refreshToken", "user" });
            doc.RootElement.GetProperty("user").EnumerateObject().Select(p => p.Name)
                .Should().BeEquivalentTo(new[] { "userId", "active_role", "available_roles" });
        }
    }

    // -----------------------------------------------------------------
    // G10 — FAIL-CLOSED identity resolution (Security:Auth:FailClosedIdentityResolve).
    //   Repro of lease jeeb-20260613002036-8874 (S02/H-B2): UseUpstream:UserManagement
    //   ON + UM phone find-or-create faults. With the flag ON the verify read path MUST
    //   fail closed (503 otp_unavailable) instead of silently minting the stale in-memory
    //   identity with a single 'client' role.
    // -----------------------------------------------------------------

    [Fact]
    public async Task G10_Verify_FailClosedOn_UmFault_Returns503_OtpUnavailable_NoToken()
    {
        var stub = new StubServiceOtpClient(); // validate succeeds (code accepted upstream)
        var um = new ThrowingDualRoleClient((int)HttpStatusCode.BadGateway);
        using var factory = MakeFactory(stub, otpEnabled: true,
            userManagementEnabled: true, failClosedIdentityResolve: true, dualRoleClient: um);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000010", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "fail-closed: a UM identity fault MUST NOT downgrade to the stale in-memory identity");
        um.PhoneCalls.Should().Be(1, "the verify read path must have dialed UM before failing closed");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("otp_unavailable", "the frozen fail-closed machine code");
        raw.Should().Contain("https://problems.jeeb.lb/auth/otp_unavailable");
        raw.ToLowerInvariant().Should().NotContain("accesstoken",
            "no half-resolved session may be minted when identity resolution fails closed");
    }

    [Fact]
    public async Task G11_Verify_FailClosedOff_UmFault_Falls_Back_And_Returns200_LegacyDegrade()
    {
        // Default-off preserves the legacy fail-SAFE degrade: a UM blip still yields a
        // live session from the in-memory store (the historical behavior).
        var stub = new StubServiceOtpClient();
        var um = new ThrowingDualRoleClient((int)HttpStatusCode.BadGateway);
        using var factory = MakeFactory(stub, otpEnabled: true,
            userManagementEnabled: true, failClosedIdentityResolve: false, dualRoleClient: um);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+9613000011", "code": "1234" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "with the flag OFF a UM blip must NOT hard-break a live login (legacy fail-safe)");
        um.PhoneCalls.Should().Be(1, "UM is still dialed first; only the fallback differs");

        var body = await resp.Content.ReadFromJsonAsync<OtpVerifyResponseDto>();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace("the in-memory fallback still mints a session");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private static IReadOnlyDictionary<string, IEnumerable<string>> HeadersWith(string name, string value)
        => new Dictionary<string, IEnumerable<string>> { [name] = new[] { value } };

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub, bool otpEnabled) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = otpEnabled);
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub,
        bool otpEnabled,
        bool userManagementEnabled,
        bool failClosedIdentityResolve,
        JeebGateway.Users.IUserManagementDualRoleClient dualRoleClient) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);

                // Swap the real HTTP dual-role client for a controllable fake so we can
                // simulate a user-management fault on the verify read path.
                services.RemoveAll<JeebGateway.Users.IUserManagementDualRoleClient>();
                services.AddSingleton(dualRoleClient);

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = otpEnabled;
                    f.UserManagement = userManagementEnabled;
                });
                services.Configure<FailClosedIdentityResolveOptions>(
                    o => o.FailClosedIdentityResolve = failClosedIdentityResolve);
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Test double for <see cref="JeebGateway.Users.IUserManagementDualRoleClient"/>
    /// whose phone find-or-create always throws a <see cref="UserManagementCallException"/>
    /// with the given upstream status — exercising the fail-closed vs fail-safe branch
    /// in <c>AuthOtpController.ResolveIdentityAsync</c>.
    /// </summary>
    private sealed class ThrowingDualRoleClient : JeebGateway.Users.IUserManagementDualRoleClient
    {
        private readonly int _status;
        public int PhoneCalls { get; private set; }

        public ThrowingDualRoleClient(int status) => _status = status;

        public Task<JeebGateway.Users.PhoneFindOrCreateResult> PhoneFindOrCreateAsync(
            string phone, CancellationToken ct)
        {
            PhoneCalls++;
            throw new JeebGateway.Users.UserManagementCallException("phone/find-or-create", _status);
        }

        public Task<JeebGateway.Users.RoleSwitchReissueResult> RoleSwitchAsync(
            string userId, string opaqueRole, CancellationToken ct)
            => throw new NotSupportedException("not exercised by the fail-closed identity tests");

        public Task<JeebGateway.Users.RoleGrantResult> AppendAvailableRoleAsync(
            string userId, string opaqueRole, CancellationToken ct)
            => throw new NotSupportedException("not exercised by the fail-closed identity tests");
    }

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public int SendCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public string? LastSendApplicationId { get; private set; }
        public string? LastValidateApplicationId { get; private set; }
        public ApiException? SendThrows { get; init; }
        public ApiException? ValidateThrows { get; init; }

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            LastSendApplicationId = body?.ApplicationId;
            if (SendThrows is not null) throw SendThrows;
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            LastValidateApplicationId = body?.ApplicationId;
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class OtpRequestResponseDto
    {
        public int TtlSeconds { get; set; }
    }

    private sealed class OtpVerifyResponseDto
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public OtpVerifyUserDto? User { get; set; }
    }

    private sealed class OtpVerifyUserDto
    {
        public string? UserId { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("active_role")]
        public string? ActiveRole { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("available_roles")]
        public string[]? AvailableRoles { get; set; }
    }
}
