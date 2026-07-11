using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-249 (info-leak, UserPreferences residual of JEBV4-63): every
/// <c>catch (RemoteUserPreferencesApiException)</c> did
/// <c>Problem(statusCode: ex.StatusCode, detail: ex.Message, ...)</c> — the JEBV4-63 partial
/// fix wrapped the leak in an RFC 7807 envelope but still forwarded the raw upstream
/// ex.Message as <c>detail</c> AND never clamped the status. The fix routes all 13 catches
/// through <c>UpstreamProblem(RemoteUserPreferencesApiException)</c> (server-side log only,
/// status clamped). Mirrors <see cref="ChatControllerErrorShapeTests"/>.
/// </summary>
public class UserPreferencesUpstreamSanitizationTests
{
    private const string Canary =
        "System.Data.Common.DbException: SECRET_CANARY_prefs17 Host=10.0.0.7;Password=s3cr3t";

    [Fact]
    public async Task GetItems_UpstreamServerError_Is_Sanitized_ProblemJson_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithPreferencesStub(stub);
        var client = MintBearerClient(factory, "prefs-user-jebv4-249");

        var resp = await client.GetAsync("/api/UserPreferences/data/favourites");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json",
            "the sanitized Problem() must emit RFC 7807 problem+json");

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("Upstream user-preferences error");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_prefs17",
            "the upstream ex.Message/body must never reach the client (JEBV4-249)");
        raw.Should().NotContain("Password=s3cr3t");
        raw.Should().NotContain("The HTTP status code of the response was not expected");
    }

    [Fact]
    public async Task GetItems_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithPreferencesStub(stub);
        var client = MintBearerClient(factory, "prefs-user-jebv4-249");

        var resp = await client.GetAsync("/api/UserPreferences/data/favourites");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502 (was forwarded raw before)");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_prefs17");
    }

    /// <summary>
    /// Source-scan regression guard: zero LIVE <c>detail: ex.Message</c>; every
    /// <c>catch (RemoteUserPreferencesApiException)</c> pairs 1:1 with <c>UpstreamProblem(ex)</c>.
    /// </summary>
    [Fact]
    public void UserPreferencesController_Source_All_Catches_Are_Sanitized()
    {
        var path = LocateSource("UserPreferencesController.cs");
        path.Should().NotBeNull();
        var liveCode = LiveCode(path!);

        Count(liveCode, "detail: ex.Message").Should().Be(0,
            "JEBV4-249: no catch may echo the upstream ex.Message on the wire");

        var catches = Count(liveCode, "catch (RemoteUserPreferencesApiException");
        var sanitized = Count(liveCode, "UpstreamProblem(ex)");
        catches.Should().BeGreaterThan(0, "the guard must actually see the upstream catch sites");
        sanitized.Should().Be(catches,
            "every catch (RemoteUserPreferencesApiException) must return UpstreamProblem(ex)");
    }

    private static WebApplicationFactory<Program> NewFactoryWithPreferencesStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceRemoteUserPreferencesClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://prefs.test/") };
                    return new ServiceRemoteUserPreferencesClient("http://prefs.test/", http);
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
}
