using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-63 (contract-audit finding 4): UserController used to carry 16 bare-string
/// <c>BadRequest("Request body cannot be null")</c> sites and 23 bare-string
/// <c>StatusCode(500, $"...: {ex.Message}")</c> catches that leaked raw exception
/// text and bypassed the gateway's own RFC7807 investment
/// (<see cref="JeebGateway.Infrastructure.UpstreamExceptionHandler"/>, wired via
/// <c>AddProblemDetails()</c> + <c>UseExceptionHandler()</c> in Program.cs). This
/// suite pins the fixed shape: a genuinely unhandled exception now reaches the
/// global handler and comes back as <c>application/problem+json</c>, never a bare
/// 500 string with <c>ex.Message</c> in it.
/// </summary>
public class UserControllerErrorShapeTests
{
    [Fact]
    public async Task GetAllUsers_UnhandledException_Yields_ProblemDetails_Via_Global_Handler()
    {
        // The stub throws a plain Exception from inside the upstream call — exactly
        // the "genuinely unhandled" case UpstreamExceptionHandler exists for. Before
        // the fix, UserController's own catch(Exception ex) would have intercepted
        // this and returned StatusCode(500, $"Error retrieving all users: {ex.Message}")
        // — a bare string leaking the raw message. That catch is now deleted, so the
        // exception reaches the global handler instead.
        var stub = new ThrowingHandler("upstream connection reset unexpectedly");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceUserManagementClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://um.test/") };
                    return new ServiceUserManagementClient("http://um.test/", http);
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "admin-jebv4-63");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var resp = await client.GetAsync("/api/User/all");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "an unhandled exception must be mapped by UpstreamExceptionHandler, not leak a bare 500 string");

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("An unexpected error occurred.");
        // The raw exception text must NOT be echoed to the client (information-disclosure).
        var raw = System.Text.Json.JsonSerializer.Serialize(problem);
        raw.Should().NotContain("upstream connection reset unexpectedly");
    }

    [Fact]
    public async Task Register_NullBody_Returns_ProblemDetails_Not_Bare_String()
    {
        var stub = new ThrowingHandler("must not reach upstream for a null body");

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceUserManagementClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://um.test/") };
                    return new ServiceUserManagementClient("http://um.test/", http);
                });
            });
        });

        var client = factory.CreateClient();

        // Empty body deserializes RegisterUserRequest as null -> the null-body guard.
        var resp = await client.PostAsync("/api/User/register",
            new StringContent("null", System.Text.Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        // A literal `null` body is intercepted by [ApiController] model validation BEFORE
        // the action runs, yielding the framework's automatic ValidationProblemDetails —
        // which is ALREADY the RFC7807 envelope (title "One or more validation errors
        // occurred."). The in-action guard (now a ProblemDetails with type
        // https://jeeb.dev/errors/request-body-required) is defence-in-depth. Either way,
        // the client sees a ProblemDetails object, never the old bare
        // "Request body cannot be null" string.
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadRequest);
        problem.Title.Should().NotBeNullOrWhiteSpace();
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("Request body cannot be null",
            "the old bare-string body must be gone");
        raw.Should().StartWith("{", "the 400 body must be a JSON ProblemDetails envelope, not a bare string");
    }

    [Fact]
    public async Task GetAllUsers_UpstreamApiException_Relayed_As_ProblemDetails()
    {
        // A well-formed upstream failure (UserManagementApiException, e.g. a 503 from
        // user-management) goes through HandleUpstreamException, which previously did
        // `StatusCode(ex.StatusCode, ex.Message)` — also a bare string. Pins the fix.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("user-management overloaded", System.Text.Encoding.UTF8, "text/plain")
            });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceUserManagementClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://um.test/") };
                    return new ServiceUserManagementClient("http://um.test/", http);
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "admin-jebv4-63");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var resp = await client.GetAsync("/api/User/all");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        // Class-level [Produces("application/json")] keeps the header application/json;
        // the BODY is the RFC7807 envelope (was a bare ex.Message string before JEBV4-63).
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        problem.Title.Should().Be("Upstream user-management error");
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_handler(request));
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly string _message;
        public ThrowingHandler(string message) => _message = message;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException(_message);
    }
}
