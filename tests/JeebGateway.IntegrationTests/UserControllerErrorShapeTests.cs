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
    public async Task GetAllUsers_UpstreamApiException_Relayed_As_Sanitized_ProblemDetails()
    {
        // A well-formed upstream failure (UserManagementApiException, e.g. a 503 from
        // user-management) goes through UpstreamProblem. JEBV4-249 (residual of JEBV4-63):
        // the prior partial fix wrapped the leak in an RFC 7807 envelope but STILL forwarded
        // the raw upstream ex.Message as `detail`. The NSwag ApiException.Message embeds the
        // upstream body, so a canary planted in the response body must NOT reach the client.
        const string canary = "SECRET_CANARY_um91 user-management down: Host=10.0.0.5;Password=hunter2";
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(canary, System.Text.Encoding.UTF8, "text/plain")
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

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the upstream status is preserved (in [400,600))");

        // JEBV4-249 / #254 content-type lock: with the class-level [Produces("application/json")]
        // removed, the in-action Problem() ObjectResult now serializes as the RFC 7807
        // application/problem+json (was downgraded to application/json by the attribute).
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.ServiceUnavailable);
        problem.Title.Should().Be("Upstream user-management error");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_um91",
            "the upstream ex.Message/body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("Password=hunter2", "no upstream connection detail may leak");
        raw.Should().NotContain("The HTTP status code of the response was not expected",
            "the NSwag ApiException.Message wrapper must not be echoed either");
    }

    [Fact]
    public async Task GetAllUsers_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        // An upstream status outside [400,600) (e.g. a stray 302) must be clamped to 502,
        // never forwarded, and must not leak the upstream body.
        const string canary = "SECRET_CANARY_umClamp redirect-loop internal detail";
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302
            {
                Content = new StringContent(canary, System.Text.Encoding.UTF8, "text/plain")
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

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502");
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_umClamp");
    }

    /// <summary>
    /// Source-scan regression guard (JEBV4-249): pins zero LIVE <c>detail: ex.Message</c> in
    /// UserController, that the class-level <c>[Produces("application/json")]</c> downgrade is
    /// gone, and that every delegating <c>catch (UserManagementApiException)</c> routes through
    /// <c>UpstreamProblem(ex)</c>. The single SuperLoginUsers roster-load catch has its own
    /// sanitized 502 handler and is the one non-delegating upstream catch.
    /// </summary>
    [Fact]
    public void UserController_Source_Has_No_Live_Upstream_Detail_Leak_And_No_Produces_Downgrade()
    {
        var path = LocateSource("UserController.cs");
        path.Should().NotBeNull("src/JeebGateway/Controllers/UserController.cs must be locatable");
        var liveCode = LiveCode(path!);

        Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no catch may echo the upstream ex.Message/body on the wire");
        Count(liveCode, "[Produces(\"application/json\")]").Should().Be(0,
            "#254: the class-level application/json downgrade must be removed so Problem() emits problem+json");

        var catches = Count(liveCode, "catch (UserManagementApiException");
        var sanitized = Count(liveCode, "UpstreamProblem(ex)");
        catches.Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");
        sanitized.Should().Be(catches - 1,
            "every catch (UserManagementApiException) delegates to UpstreamProblem(ex) EXCEPT the single "
            + "SuperLoginUsers roster-load catch, which has its own sanitized 502 handler");
    }

    private static string? LocateSource(string controllerFileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway", "Controllers", controllerFileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string LiveCode(string path)
        => string.Join("\n",
            File.ReadAllLines(path).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
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
