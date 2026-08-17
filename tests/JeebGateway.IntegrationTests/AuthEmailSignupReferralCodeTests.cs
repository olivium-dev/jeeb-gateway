using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Regression for the live public-signup outage: <c>POST /v1/auth/signup</c>
/// mapped the caller onto UM <c>RegisterUserRequest</c> WITHOUT
/// <c>ReferralCode</c>, which live user-management requires
/// (<c>400 {"errors":{"ReferralCode":["The ReferralCode field is required."]}}</c>).
/// Email signup was therefore dead on live for every caller, mobile included,
/// while <c>DevController</c> had already defaulted the same field.
///
/// These tests pin the wire payload the gateway sends upstream, using the same
/// stub-handler seam as <see cref="DevEndpointsTests"/> so no live UM is needed.
/// </summary>
public class AuthEmailSignupReferralCodeTests
{
    /// <summary>
    /// The failing case: a caller that omits referralCode (mobile's signup body
    /// has no such field) must STILL produce a present, non-null referralCode on
    /// the wire, so the upstream insert succeeds instead of 400ing.
    /// </summary>
    [Fact]
    public async Task Signup_NoReferralCodeSupplied_StillSendsReferralCode()
    {
        var captured = new CapturedBody();
        using var factory = NewFactory(StubReturning(captured, """
            { "userId": "um-signup-id-001", "email": "newuser@jeeb.test", "status": "created" }
            """));
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/v1/auth/signup", JsonBody("""
            { "email": "newuser@jeeb.test", "password": "S3cret-passw0rd!", "name": "New User" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "with referralCode present the upstream register succeeds and a session is minted");

        captured.Path.Should().EndWith("/api/User/register");
        captured.Body.Should().Contain("\"referralCode\":\"\"",
            "an omitted referralCode must be sent as an empty string so UM's required/non-null check passes");
    }

    /// <summary>Negative control: the assertion above can fail. The identical
    /// harness observes a body with NO referralCode when the caller hits a route
    /// that does not map it — proving the check reads the real wire payload
    /// rather than always matching.</summary>
    [Fact]
    public async Task Control_LoginBody_CarriesNoReferralCode()
    {
        var captured = new CapturedBody();
        using var factory = NewFactory(StubReturning(captured, """
            { "userId": "um-login-id-001", "email": "existing@jeeb.test" }
            """));
        var client = factory.CreateClient();

        await client.PostAsync("/v1/auth/login", JsonBody("""
            { "email": "existing@jeeb.test", "password": "S3cret-passw0rd!" }
            """));

        captured.Path.Should().EndWith("/api/User/login");
        captured.Body.Should().NotContain("referralCode",
            "login does not register a user, so the same probe returns the opposite answer");
    }

    /// <summary>A caller-supplied referralCode is forwarded verbatim (trimmed).</summary>
    [Fact]
    public async Task Signup_ReferralCodeSupplied_ForwardsItTrimmed()
    {
        var captured = new CapturedBody();
        using var factory = NewFactory(StubReturning(captured, """
            { "userId": "um-signup-id-002", "email": "referred@jeeb.test", "status": "created" }
            """));
        var client = factory.CreateClient();

        var resp = await client.PostAsync("/v1/auth/signup", JsonBody("""
            { "email": "referred@jeeb.test", "password": "S3cret-passw0rd!", "name": "Referred", "referralCode": "  REF123  " }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Body.Should().Contain("\"referralCode\":\"REF123\"",
            "a caller-supplied referralCode is forwarded verbatim after trimming");
    }

    // ---------------------------------------------------------------- harness

    private sealed class CapturedBody
    {
        public string Body { get; set; } = "";
        public string Path { get; set; } = "";
    }

    private static StubHandler StubReturning(CapturedBody captured, string json) =>
        new(req =>
        {
            captured.Path = req.RequestUri!.AbsolutePath;
            captured.Body = req.Content is null
                ? ""
                : req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
        });

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private static StringContent JsonBody(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private static WebApplicationFactory<Program> NewFactory(HttpMessageHandler upstreamHandler)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<ServiceUserManagementClient>();
                    services.AddScoped(_ =>
                    {
                        var http = new HttpClient(upstreamHandler)
                        {
                            BaseAddress = new Uri("http://um.test/"),
                        };
                        return new ServiceUserManagementClient("http://um.test/", http);
                    });
                });
            });
    }
}
