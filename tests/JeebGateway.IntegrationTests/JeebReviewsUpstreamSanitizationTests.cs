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
/// JEBV4-249 (info-leak, JeebReviews residual): the submit + reveal catches did
/// <c>Problem(detail: ex.Message, ...)</c>, leaking the NSwag feedback-service
/// <see cref="ApiException"/>.Message. The fix routes those two GENERAL catches through
/// <c>UpstreamProblem(FeedbackApiException)</c>. The graceful reviews-list degrade (returns the
/// cold-start empty page), the <c>when (404) → NotFound()</c> un-rated mapping, and the local
/// <c>catch (ArgumentException) → Problem400(ex.Message)</c> tag validation are unchanged.
/// Mirrors <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class JeebReviewsUpstreamSanitizationTests
{
    private const string CallerGuid = "44444444-4444-4444-4444-444444444444";
    private const string JeeberGuid = "55555555-5555-5555-5555-555555555555";
    private const string Canary =
        "System.Exception: SECRET_CANARY_review73 at FeedbackService.Blind.Submit() line 12";

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
            "/v1/ratings/jeeb/submit",
            JsonContent.Create(new { deliveryId, score = 5 }));

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The reviews request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_review73",
            "the upstream feedback-service body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("The HTTP status code of the response was not expected");
        raw.Should().StartWith("{");
    }

    [Fact]
    public async Task ListReviews_UpstreamFailure_Degrades_To_Empty_Page_Without_Leaking_Body()
    {
        // The reviews-list catch is a deliberate graceful degrade (cold-start empty page), NOT
        // routed through UpstreamProblem. It must stay a 200 and must not leak the upstream body.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithFeedbackStub(stub);
        var client = MintBearerClient(factory, CallerGuid);

        var resp = await client.GetAsync($"/v1/ratings/jeeb/reviews?jeeberId={JeeberGuid}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "an upstream reviews-list failure degrades to the empty page, not a client-facing error");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_review73");
    }

    /// <summary>
    /// Source-scan regression guard: zero LIVE <c>detail: ex.Message</c>; the two GENERAL
    /// submit/reveal <c>catch (FeedbackApiException)</c> sites route through
    /// <c>UpstreamProblem(ex)</c>; the graceful reviews-list degrade and the 404 mapper remain.
    /// </summary>
    [Fact]
    public void JeebReviewsController_Source_General_Catches_Are_Sanitized()
    {
        var path = ControllerSourceScan.Locate("JeebReviewsController.cs");
        path.Should().NotBeNull();
        var liveCode = ControllerSourceScan.LiveCode(path!);

        ControllerSourceScan.Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no general catch may echo the feedback-service ex.Message on the wire");

        ControllerSourceScan.Count(liveCode, "catch (FeedbackApiException")
            .Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");
        ControllerSourceScan.Count(liveCode, "UpstreamProblem(ex)").Should().Be(2,
            "the two GENERAL submit/reveal catches must route through UpstreamProblem(ex)");

        // Behaviour-preserving branches must stay.
        ControllerSourceScan.Count(liveCode, "EmptyReviewsPage").Should().BeGreaterThan(0,
            "the reviews-list graceful cold-start degrade must remain");
        ControllerSourceScan.Count(liveCode, "when (ex.StatusCode == StatusCodes.Status404NotFound)")
            .Should().Be(1, "the un-rated 404 → NotFound() mapper must remain");
    }

    private static async Task<string> SeedDeliveryAsync(
        WebApplicationFactory<Program> factory, string clientId, string jeeberId)
    {
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
