using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 (info-leak, JeebNotificationsInbox residual): the GENERAL inbox-read and
/// mark-read catches did <c>Problem(detail: ex.Message, ...)</c>, leaking the NSwag
/// <see cref="ApiException"/>.Message (which wraps the upstream notification-service body).
/// The fix routes the general catches through <c>UpstreamProblem(NotificationApiException)</c>;
/// the deliberate <c>when (401 or 403) → Unauthorized()</c> and <c>when (404) → NotFound()</c>
/// status mappers are unchanged. Mirrors <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class JeebNotificationsInboxUpstreamSanitizationTests
{
    private const string Canary =
        "System.InvalidOperationException: SECRET_CANARY_notif88 at NotificationService.Receiver.Load() line 7";

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"items\":{}}")]
    [InlineData("{\"items\":[null]}")]
    [InlineData("{\"items\":[42]}")]
    [InlineData("{\"items\":[],\"total\":-1}")]
    [InlineData("{\"items\":[],\"total\":\"10\"}")]
    [InlineData("{\"items\":[{\"id\":\"n\",\"type\":\"chat\",\"requestId\":\"/\"}]}")]
    [InlineData("{\"items\":[{\"id\":\"n\",\"type\":\"chat\",\"requestId\":42}]}")]
    [InlineData("{\"items\":[{\"id\":\"n\",\"type\":\"chat\",\"requestId\":\"valid\",\"payload\":{\"deepLink\":\"https://secret.invalid/SECRET_CANARY_notif88\"}}]}")]
    public async Task Malformed_Upstream_Success_Is_502_Not_An_Empty_Inbox(string wire)
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wire, Encoding.UTF8, "application/json")
        });
        using var factory = NewFactoryWithNotificationStub(stub);
        using var client = MintBearerClient(factory, "notif-contract-user");
        var response = await client.GetAsync("/v1/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await response.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be(502);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("SECRET_CANARY_notif88").And.NotContain("\"items\"");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"items\":[],\"total\":0}")]
    public async Task Explicit_Empty_Upstream_List_Remains_A_Valid_Empty_Page(string wire)
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wire, Encoding.UTF8, "application/json")
        });
        using var factory = NewFactoryWithNotificationStub(stub);
        using var client = MintBearerClient(factory, "notif-contract-user");
        var response = await client.GetAsync("/v1/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JeebGateway.JeebNotifications.JeebNotificationsPageResponse>();
        page!.Items.Should().BeEmpty();
        page.TotalCount.Should().Be(0);
    }

    [Theory]
    [InlineData("/chat/req-1/?source=inbox")]
    [InlineData("jeeb://orders/req-1/receipt/?source=inbox")]
    [InlineData("jeeb://wallet/")]
    [InlineData("/")]
    public async Task Supported_Explicit_Link_Forms_Remain_200_Through_Real_Client_And_Host(string link)
    {
        var wire = new Newtonsoft.Json.Linq.JObject
        {
            ["items"] = new Newtonsoft.Json.Linq.JArray(new Newtonsoft.Json.Linq.JObject
            {
                ["id"] = "n-1", ["type"] = "chat", ["requestId"] = "req-1",
                ["payload"] = new Newtonsoft.Json.Linq.JObject { ["deepLink"] = link }
            })
        };
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(wire.ToString(), Encoding.UTF8, "application/json")
        });
        using var factory = NewFactoryWithNotificationStub(stub);
        using var client = MintBearerClient(factory, "notif-contract-user");
        var response = await client.GetAsync("/v1/notifications");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<JeebGateway.JeebNotifications.JeebNotificationsPageResponse>();
        page!.Items.Should().ContainSingle().Which.DeepLink.Should().Be(link);
    }

    [Fact]
    public async Task MarkRead_Malformed_Ownership_List_Is_502_Without_Mutation()
    {
        var methods = new List<HttpMethod>();
        var stub = new StubHttpMessageHandler(request =>
        {
            methods.Add(request.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });
        using var factory = NewFactoryWithNotificationStub(stub);
        using var client = MintBearerClient(factory, "notif-contract-user");
        var response = await client.PatchAsync("/v1/notifications/n-1/read", null);
        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        methods.Should().Equal(HttpMethod.Get);
    }

    [Fact]
    public async Task ListNotifications_UpstreamServerError_Is_Sanitized_ProblemDetails_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory, "notif-user-jebv4-249");

        var resp = await client.GetAsync("/v1/notifications");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The notifications request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_notif88",
            "the upstream response body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("The HTTP status code of the response was not expected");
        raw.Should().StartWith("{");
    }

    [Fact]
    public async Task ListNotifications_Upstream401_Stays_401_And_Does_Not_Leak_Body()
    {
        // The deliberate `when (401 or 403) → Unauthorized()` mapper must be preserved: the
        // caller authenticated at the gateway, so a 401 here is the sanitized upstream mapping,
        // and it must not leak the upstream body.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory, "notif-user-jebv4-249");

        var resp = await client.GetAsync("/v1/notifications");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the filtered 401/403 → Unauthorized() mapping must remain intact");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_notif88");
    }

    /// <summary>
    /// Source-scan regression guard: zero LIVE <c>detail: ex.Message</c>; the two GENERAL
    /// catches route through <c>UpstreamProblem(ex)</c>; and the deliberate filtered status
    /// mappers (401/403 → Unauthorized, 404 → NotFound) remain.
    /// </summary>
    [Fact]
    public void JeebNotificationsInboxController_Source_General_Catches_Are_Sanitized()
    {
        var path = ControllerSourceScan.Locate("JeebNotificationsInboxController.cs");
        path.Should().NotBeNull();
        var liveCode = ControllerSourceScan.LiveCode(path!);

        ControllerSourceScan.Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no general catch may echo the upstream ex.Message on the wire");

        ControllerSourceScan.Count(liveCode, "catch (NotificationApiException")
            .Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");
        ControllerSourceScan.Count(liveCode, "UpstreamProblem(ex)").Should().Be(2,
            "both GENERAL catches (inbox-read + mark-read) must route through UpstreamProblem(ex)");

        // Behaviour-preserving filtered mappers must stay.
        ControllerSourceScan.Count(liveCode, "when (ex.StatusCode is 401 or 403)").Should().Be(2);
        ControllerSourceScan.Count(liveCode, "when (ex.StatusCode == 404)").Should().Be(1);
    }

    private static WebApplicationFactory<Program> NewFactoryWithNotificationStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceNotificationClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://notif.test/") };
                    return new ServiceNotificationClient("http://notif.test/", http);
                });
            });
        });

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory, string sub)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", sub), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }
}
