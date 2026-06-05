using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Controllers;
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
/// Tests for the one-time-password gateway integration (feat/add-one-time-password).
///
/// Two layers:
///   1. CONTRACT — drives the REAL NSwag-generated <see cref="ServiceOTPClient"/>
///      against a fake <see cref="HttpMessageHandler"/>, locking the exact
///      upstream paths the gateway calls (<c>api/User/SendOTP</c> /
///      <c>api/User/ValidateOTP</c>) and the request-body shape. This is the
///      same client <see cref="DeliveriesController"/> uses for the 4-digit
///      delivery_handover OTP.
///   2. REGISTRATION + ENDPOINT — boots the gateway via
///      <see cref="WebApplicationFactory{TEntryPoint}"/> with a stub
///      <see cref="IServiceOTPClient"/> and asserts the thin
///      <see cref="OtpController"/> (POST /api/otp/send, /api/otp/validate) is
///      wired, gated by <c>FeatureFlags:UseUpstream:Otp</c>, and shapes errors
///      as RFC 7807 ProblemDetails.
///
/// Serves JEB-1471, JEB-1467, JEB-1459, JEB-1455, JEB-1441, JEB-1437, JEB-1433,
/// JEB-1430, JEB-626, JEB-625, JEB-471, JEB-158, JEB-159, JEB-55, JEB-49,
/// JEB-37, JEB-38, JEB-39.
/// </summary>
public class OtpEndpointTests
{
    // ---------------------------------------------------------------------
    // 1. CONTRACT — real NSwag client over a fake handler
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ServiceOtpClient_SendOTP_Hits_ApiUser_SendOTP_With_Body()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, req =>
        {
            captured = req;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        }))
        {
            BaseAddress = new Uri("http://otp.test/")
        };

        var client = new ServiceOTPClient("http://otp.test/", http);

        await client.SendOTPAsync(new SendOTPRequestUserID
        {
            PhoneNumber = "+9613000000",
            ApplicationId = "delivery_handover_del_1"
        });

        captured.Should().NotBeNull();
        captured!.Method.Should().Be(HttpMethod.Post);
        captured.RequestUri!.AbsolutePath.Should().Be("/api/User/SendOTP");
        // System.Text.Json escapes '+' as + by default; assert on the
        // escaped form the NSwag client actually puts on the wire.
        body.Should().Contain("\"phoneNumber\":\"\\u002B9613000000\"");
        body.Should().Contain("\"applicationId\":\"delivery_handover_del_1\"");
    }

    [Fact]
    public async Task ServiceOtpClient_ValidateOTP_Hits_ApiUser_ValidateOTP_With_Body()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.OK, req =>
        {
            captured = req;
            body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        }))
        {
            BaseAddress = new Uri("http://otp.test/")
        };

        var client = new ServiceOTPClient("http://otp.test/", http);

        await client.ValidateOTPAsync(new ValidateOTPRequestModel
        {
            PhoneNumber = "+9613000000",
            Otp = "1234",
            ApplicationId = "delivery_handover_del_1"
        });

        captured.Should().NotBeNull();
        captured!.RequestUri!.AbsolutePath.Should().Be("/api/User/ValidateOTP");
        body.Should().Contain("\"otp\":\"1234\"");
    }

    [Fact]
    public async Task ServiceOtpClient_NonSuccess_Throws_ApiException()
    {
        var http = new HttpClient(new CapturingHandler(HttpStatusCode.Unauthorized, _ => { }))
        {
            BaseAddress = new Uri("http://otp.test/")
        };
        var client = new ServiceOTPClient("http://otp.test/", http);

        var act = async () => await client.ValidateOTPAsync(new ValidateOTPRequestModel
        {
            PhoneNumber = "+9613000000",
            Otp = "9999",
            ApplicationId = "app"
        });

        var ex = (await act.Should().ThrowAsync<ApiException>()).Which;
        ex.StatusCode.Should().Be((int)HttpStatusCode.Unauthorized);
    }

    // ---------------------------------------------------------------------
    // 2. REGISTRATION + ENDPOINT — thin controller over a stub client
    // ---------------------------------------------------------------------

    [Fact]
    public async Task Send_When_Flag_On_Returns_202_And_Calls_Upstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000000",
            ApplicationId: "app-1"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.SendCalls.Should().Be(1);
    }

    [Fact]
    public async Task Validate_When_Flag_On_Returns_200_And_Calls_Upstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/validate", new OtpValidateRequest(
            PhoneNumber: "+9613000000",
            Otp: "1234",
            ApplicationId: "app-1"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.ValidateCalls.Should().Be(1);
    }

    [Fact]
    public async Task Validate_Upstream_401_Maps_To_401_ProblemDetails()
    {
        var stub = new StubServiceOtpClient
        {
            ValidateThrows = new ApiException(
                "unauthorized", (int)HttpStatusCode.Unauthorized, null, EmptyHeaders, null)
        };
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/validate", new OtpValidateRequest(
            PhoneNumber: "+9613000000",
            Otp: "9999",
            ApplicationId: "app-1"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Status.Should().Be(401);
        // Upstream body must NEVER be echoed to the caller.
        problem.Detail.Should().NotContain("9999");
    }

    [Fact]
    public async Task Send_When_Flag_Off_Returns_503_Without_Calling_Upstream()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: false);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000000",
            ApplicationId: "app-1"));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        stub.SendCalls.Should().Be(0);
    }

    [Fact]
    public async Task Send_With_Missing_Phone_Returns_400_ProblemDetails()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "",
            ApplicationId: ""));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        stub.SendCalls.Should().Be(0);
    }

    // JEB-1516 H6-A regression: a non-GUID applicationId (the legacy "b05"
    // partition token) must NOT 502/400 — the gateway defaults to the configured
    // Jeeb tenant GUID and forwards a parseable GUID to the shared service.
    [Fact]
    public async Task Send_With_NonGuid_ApplicationId_Defaults_To_Configured_Guid()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true, applicationId: JeebTenantGuid);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000001",
            ApplicationId: "b05"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.SendCalls.Should().Be(1);
        stub.LastSendApplicationId.Should().Be(JeebTenantGuid);
    }

    [Fact]
    public async Task Send_With_Omitted_ApplicationId_Defaults_To_Configured_Guid()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true, applicationId: JeebTenantGuid);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000001",
            ApplicationId: null));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.SendCalls.Should().Be(1);
        stub.LastSendApplicationId.Should().Be(JeebTenantGuid);
    }

    [Fact]
    public async Task Send_With_WellFormed_Guid_Is_Honoured_Verbatim()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true, applicationId: JeebTenantGuid);
        var http = factory.CreateClient();

        var caller = "11111111-2222-3333-4444-555555555555";
        var resp = await http.PostAsJsonAsync("/api/otp/send", new OtpSendRequest(
            PhoneNumber: "+9613000001",
            ApplicationId: caller));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        stub.LastSendApplicationId.Should().Be(caller);
    }

    [Fact]
    public async Task Validate_With_NonGuid_ApplicationId_Defaults_To_Configured_Guid()
    {
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, otpEnabled: true, applicationId: JeebTenantGuid);
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/api/otp/validate", new OtpValidateRequest(
            PhoneNumber: "+9613000001",
            Otp: "1234",
            ApplicationId: "b05"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.ValidateCalls.Should().Be(1);
        stub.LastValidateApplicationId.Should().Be(JeebTenantGuid);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private const string JeebTenantGuid = "0d51afe1-499f-4a29-a55a-36d2dd223b05";

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub, bool otpEnabled, string? applicationId = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f => f.Otp = otpEnabled);
                if (applicationId is not null)
                {
                    services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(
                        o => o.ApplicationId = applicationId);
                }
            });
        });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Action<HttpRequestMessage> _onRequest;

        public CapturingHandler(HttpStatusCode status, Action<HttpRequestMessage> onRequest)
        {
            _status = status;
            _onRequest = onRequest;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest(request);
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent("{}")
            });
        }
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
}
