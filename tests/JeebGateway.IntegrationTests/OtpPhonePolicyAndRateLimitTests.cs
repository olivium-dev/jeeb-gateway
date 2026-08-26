using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Gateway-local explicit E.164 admission/canonicalisation + OTP-request burst
/// guard on <c>POST /v1/auth/otp/request</c>. These tests prove international
/// eligibility, strict fail-closed parsing, one canonical downstream identity,
/// and the security-critical invariant that a rejected/throttled request never
/// dials the upstream (<c>SendCalls == 0</c>).
///
/// The upstream is the same <see cref="StubServiceOtpClient"/> counter used by
/// <c>AuthOtpControllerTests</c>; nothing is mocked away from the real controller
/// path — the policy and limiter are the genuine production singletons.
/// </summary>
public class OtpPhonePolicyAndRateLimitTests
{
    private const string AppId = "jeeb-test-app";

    // ---------------------------------------------------------------
    // N4 — syntactically invalid phone -> 400 invalid_phone, no upstream
    // ---------------------------------------------------------------
    [Fact]
    public async Task N4_UnparseablePhone_Returns400_InvalidPhone_AndDoesNotDialUpstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+961ABC" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("invalid_phone");
        raw.Should().Contain("https://problems.jeeb.lb/auth/invalid_phone");
        stub.SendCalls.Should().Be(0, "an unparseable phone must be rejected before the upstream is dialed");
    }

    // ---------------------------------------------------------------
    // International eligibility — a valid non-LB phone is canonicalised and
    // reaches the typed OTP client exactly once even with legacy LB config.
    // ---------------------------------------------------------------
    [Fact]
    public async Task ValidNonLebanesePhone_IsAdmitted_Canonicalised_AndDialsUpstreamOnce()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+1 (415) 555-0100" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.SendCalls.Should().Be(1);
        stub.LastSendPhone.Should().Be("+14155550100",
            "the typed OTP boundary receives one canonical international value");
    }

    // ---------------------------------------------------------------
    // Parse-first ordering: a malformed number is invalid_phone, NOT
    // invalid_country (N4 vs N3 must stay distinct).
    // ---------------------------------------------------------------
    [Fact]
    public async Task MalformedNumber_IsInvalidPhone_NotInvalidCountry()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "not-a-number" }"""));

        var raw = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        raw.Should().Contain("invalid_phone");
        raw.Should().NotContain("invalid_country");
        stub.SendCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("03000001", "national format")]
    [InlineData("009613000001", "international prefix without an explicit plus")]
    [InlineData("+9611", "impossible number")]
    [InlineData("+1234567890123456", "overlong E.164")]
    [InlineData("+961+9613000001", "double prefix")]
    public async Task NonCanonicalOrImpossiblePhone_Returns400_AndNeverDialsUpstream(
        string phone, string reason)
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody(JsonSerializer.Serialize(new { phone })));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, reason);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_phone");
        stub.SendCalls.Should().Be(0, $"{reason} must fail before the typed OTP client");
    }

    // ---------------------------------------------------------------
    // Happy: a valid LB phone is admitted and DOES dial the upstream once.
    // ---------------------------------------------------------------
    [Fact]
    public async Task ValidLebanesePhone_IsAdmitted_AndDialsUpstreamOnce()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000001" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.SendCalls.Should().Be(1, "a valid LB phone must pass the policy and dial the upstream");
        stub.LastSendPhone.Should().Be("+9613000001");
    }

    // ---------------------------------------------------------------
    // Legacy typed compatibility — the controller still maps an
    // InvalidCountry policy outcome to the frozen RFC 7807 response.
    // ---------------------------------------------------------------
    [Fact]
    public async Task LegacyInvalidCountryOutcome_PreservesFrozenProblemDetailsContract()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, phonePolicy: new InvalidCountryPhonePolicy());
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+14155550100" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("https://problems.jeeb.lb/auth/invalid_country");
        stub.SendCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------
    // N12 — per-phone burst guard trips 429 rate_limited; the throttled
    //       request does NOT add an upstream call (SendCalls frozen at cap).
    // ---------------------------------------------------------------
    [Fact]
    public async Task N12_PerPhoneBurst_Returns429_RateLimited_AndDoesNotDialUpstreamWhenThrottled()
    {
        var stub = new StubServiceOtpClient();
        // Per-phone cap = 3; the 4th request for the SAME phone is throttled.
        using var factory = MakeFactory(stub, maxPerPhone: 3, maxPerIp: 100);
        var http = factory.CreateClient();

        const string body = """{ "phone": "+9613000050" }""";

        // 3 admitted requests -> 3 upstream sends.
        for (var i = 0; i < 3; i++)
        {
            var ok = await http.PostAsync("/v1/auth/otp/request", JsonBody(body));
            ok.StatusCode.Should().Be(HttpStatusCode.OK, $"request #{i + 1} is within the per-phone cap");
        }
        stub.SendCalls.Should().Be(3);

        // 4th request -> throttled.
        var throttled = await http.PostAsync("/v1/auth/otp/request", JsonBody(body));
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        var raw = await throttled.Content.ReadAsStringAsync();
        raw.Should().Contain("rate_limited");
        raw.Should().Contain("https://problems.jeeb.lb/auth/rate_limited");

        // THE critical, assertion-provable invariant: the throttled request did
        // NOT dial the upstream — SendCalls is still 3, not 4.
        stub.SendCalls.Should().Be(3, "a throttled OTP request must not cost an upstream SendOTP (no SMS)");
    }

    [Fact]
    public async Task FormattingVariants_ShareCanonicalPerPhoneThrottleBucket()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, maxPerPhone: 1, maxPerIp: 100);
        var http = factory.CreateClient();

        var first = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+1 415 555 0100" }"""));
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var samePhone = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+1 (415) 555-0100" }"""));

        samePhone.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        stub.SendCalls.Should().Be(1,
            "presentation variants must not split the canonical per-phone throttle bucket");
        stub.LastSendPhone.Should().Be("+14155550100");
    }

    // ---------------------------------------------------------------
    // N12 (IP leg) — per-IP burst guard also trips 429 rate_limited even
    //                across DIFFERENT phones from the same source IP.
    // ---------------------------------------------------------------
    [Fact]
    public async Task PerIpBurst_AcrossPhones_Returns429_RateLimited()
    {
        var stub = new StubServiceOtpClient();
        // Per-IP cap = 2 (per-phone high so the IP leg is the one that trips).
        using var factory = MakeFactory(stub, maxPerPhone: 100, maxPerIp: 2);
        var http = factory.CreateClient();

        (await http.PostAsync("/v1/auth/otp/request", JsonBody("""{ "phone": "+9613000061" }""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await http.PostAsync("/v1/auth/otp/request", JsonBody("""{ "phone": "+9613000062" }""")))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // 3rd request, a different phone, same IP -> IP window exhausted.
        var throttled = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+9613000063" }"""));
        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await throttled.Content.ReadAsStringAsync()).Should().Contain("rate_limited");
        stub.SendCalls.Should().Be(2, "the per-IP-throttled request must not dial the upstream");
    }

    [Fact]
    public async Task Verify_UsesCanonicalPhoneForTypedClientAndSessionIdentity()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var formatted = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+1 (415) 555-0100", "code": "1234" }"""));
        var compact = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody("""{ "phone": "+14155550100", "code": "1234" }"""));

        formatted.StatusCode.Should().Be(HttpStatusCode.OK);
        compact.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.ValidatePhones.Should().Equal("+14155550100", "+14155550100");

        var formattedSession = await formatted.Content.ReadFromJsonAsync<OtpVerifyResponse>();
        var compactSession = await compact.Content.ReadFromJsonAsync<OtpVerifyResponse>();
        formattedSession!.User.UserId.Should().Be(compactSession!.User.UserId,
            "canonical formatting variants must resolve the same session identity");
    }

    [Theory]
    [InlineData("4155550100")]
    [InlineData("+1234567890123456")]
    [InlineData("+1+14155550100")]
    public async Task Verify_InvalidPhone_PreservesGeneric401_AndNeverDialsUpstream(string phone)
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody(JsonSerializer.Serialize(new { phone, code = "1234" })));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("invalid_otp");
        stub.ValidateCalls.Should().Be(0);
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub,
        int maxPerPhone = 3,
        int maxPerIp = 10,
        IPhonePolicy? phonePolicy = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient,
                    Fakes.TestUserManagementDualRoleClient>();
                if (phonePolicy is not null)
                {
                    services.RemoveAll<IPhonePolicy>();
                    services.AddSingleton(phonePolicy);
                }
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
                services.Configure<PhonePolicyOptions>(o =>
                {
                    o.AllowedRegion = "LB";
                    o.EnforceRegion = true;
                });
                services.Configure<OtpRequestRateLimitOptions>(o =>
                {
                    o.Enabled = true;
                    o.MaxPerPhonePerWindow = maxPerPhone;
                    o.MaxPerIpPerWindow = maxPerIp;
                    o.WindowSeconds = 60;
                });
            });
        });

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public int SendCalls { get; private set; }
        public int ValidateCalls { get; private set; }
        public string? LastSendPhone { get; private set; }
        public List<string?> ValidatePhones { get; } = new();

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            LastSendPhone = body?.PhoneNumber;
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            ValidatePhones.Add(body?.PhoneNumber);
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InvalidCountryPhonePolicy : IPhonePolicy
    {
        public PhonePolicyResult Evaluate(string? rawPhone) => PhonePolicyResult.InvalidCountry;
    }
}
