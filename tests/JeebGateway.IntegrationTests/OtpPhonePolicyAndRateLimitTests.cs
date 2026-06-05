using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S02 F-E (JEB-37 / JEB-1422) — gateway-local phone admission policy + OTP-request
/// burst guard on <c>POST /v1/auth/otp/request</c>. These assert the THREE reject
/// outcomes the S02 strict suite freezes (N3/N4/N12) AND the security-critical
/// invariant that a rejected/throttled request NEVER dials the upstream
/// (<c>SendCalls == 0</c>) — a throttle must never cost an SMS.
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
    // N3 — non-LB (US) phone -> 400 invalid_country, no upstream
    // ---------------------------------------------------------------
    [Fact]
    public async Task N3_NonLebanesePhone_Returns400_InvalidCountry_AndDoesNotDialUpstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        // +1 415 555 0100 — a structurally valid US number, wrong country.
        var resp = await http.PostAsync("/v1/auth/otp/request",
            JsonBody("""{ "phone": "+14155550100" }"""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().Contain("invalid_country");
        raw.Should().Contain("https://problems.jeeb.lb/auth/invalid_country");
        stub.SendCalls.Should().Be(0, "a non-LB phone must be rejected before the upstream is dialed");
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

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub, int maxPerPhone = 3, int maxPerIp = 10) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = true);
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

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
        {
            SendCalls++;
            return Task.CompletedTask;
        }

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
