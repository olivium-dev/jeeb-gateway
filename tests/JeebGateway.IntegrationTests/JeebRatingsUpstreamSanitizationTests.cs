using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.service.ServiceFeedback;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 (info-leak, JeebRatings residual): the submit + reveal catches did
/// <c>Problem(detail: ex.Message, ...)</c>, leaking the NSwag feedback-service
/// <see cref="ApiException"/>.Message. The fix routes both <c>catch (FeedbackApiException)</c>
/// sites through <c>UpstreamProblem(FeedbackApiException)</c> (server-side log only). The local
/// <c>catch (ArgumentException) → Problem400(ex.Message)</c> tag-validation branch is a
/// client-supplied 400 and is intentionally left untouched. Mirrors
/// <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class JeebRatingsUpstreamSanitizationTests
{
    private const string CallerGuid = "22222222-2222-2222-2222-222222222222";
    private const string JeeberGuid = "33333333-3333-3333-3333-333333333333";
    private const string Canary =
        "System.Exception: SECRET_CANARY_rating55 at FeedbackService.Blind.Submit() line 91";

    [Fact]
    public async Task Submit_UpstreamServerError_Is_Sanitized_ProblemDetails_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithFeedbackStub(stub);
        var deliveryId = await SeedDeliveryAsync(factory, CallerGuid, JeeberGuid);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.PostAsync(
            $"/v1/ratings/jeeb/deliveries/{deliveryId}",
            JsonContent.Create(new { stars = 5 }));

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The rating request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_rating55",
            "the upstream feedback-service body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("The HTTP status code of the response was not expected");
        raw.Should().StartWith("{");
    }

    /// <summary>
    /// Source-scan regression guard: zero LIVE <c>detail: ex.Message</c>; both upstream
    /// <c>catch (FeedbackApiException)</c> sites pair 1:1 with <c>UpstreamProblem(ex)</c>.
    /// The local ArgumentException 400 uses <c>Problem400(ex.Message)</c> — a different token
    /// that is not scanned — so it is not affected.
    /// </summary>
    [Fact]
    public void JeebRatingsController_Source_All_Upstream_Catches_Are_Sanitized()
    {
        var path = ControllerSourceScan.Locate("JeebRatingsController.cs");
        path.Should().NotBeNull();
        var liveCode = ControllerSourceScan.LiveCode(path!);

        ControllerSourceScan.Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no upstream catch may echo the feedback-service ex.Message on the wire");

        var catches = ControllerSourceScan.Count(liveCode, "catch (FeedbackApiException");
        var sanitized = ControllerSourceScan.Count(liveCode, "UpstreamProblem(ex)");
        catches.Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");
        sanitized.Should().Be(catches,
            "every catch (FeedbackApiException ...) must return UpstreamProblem(ex) — a single-site revert breaks this pairing");
    }

    private static async Task<string> SeedDeliveryAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId)
    {
        // IRequestsStore is a singleton, so seeding it here is visible to the request pipeline.
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(
            new CreateRequestInput { ClientId = clientId, Description = "sanitization-test delivery" },
            CancellationToken.None);
        await store.SetJeeberIdAsync(created.Id, jeeberId, CancellationToken.None);
        return created.Id;
    }

    private static WebApplicationFactory<Program> NewFactoryWithFeedbackStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // JeebRatings is gated by FeatureFlags:UseUpstream:Ratings (default OFF → 503);
            // turn it ON so the upstream catch under test is reachable.
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureFlags:UseUpstream:Ratings"] = "true"
                }));

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceFeedbackClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://feedback.test/") };
                    return new ServiceFeedbackClient("http://feedback.test/", http);
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
