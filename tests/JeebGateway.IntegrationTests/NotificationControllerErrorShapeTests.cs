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
/// JEBV4-250 (info-leak): NotificationController carried 7 bare
/// <c>return StatusCode(ex.StatusCode, ex.Message)</c> catches. The NSwag
/// <see cref="ApiException"/>.Message embeds up to 512 chars of the raw upstream
/// notification-service response body, so every notification endpoint leaked
/// upstream exception detail to the caller. The fix routes every catch through
/// <c>NotificationController.UpstreamProblem</c>, a sanitized RFC 7807
/// ProblemDetails: the upstream status is preserved, but the upstream message/body
/// is logged server-side only and never put on the wire.
///
/// <para>Mirrors <see cref="ChatControllerErrorShapeTests"/> (the JEBV4-242
/// precedent). The public <c>/api/Notification/health</c> passthrough exercises the
/// exact <c>catch (ApiException) → UpstreamProblem</c> path shared by all 7
/// notification actions, so a raw HttpClient stub bound directly to the scoped
/// <see cref="ServiceNotificationClient"/> deterministically drives the leak path.</para>
/// </summary>
public class NotificationControllerErrorShapeTests
{
    private const string Canary =
        "System.NullReferenceException: SECRET_CANARY_n250 at NotificationService.Internal.SecretRepo.Load() line 42";

    [Fact]
    public async Task Health_UpstreamServerError_Is_Sanitized_ProblemDetails_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Notification/health");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The notification request could not be completed.");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_n250",
            "the upstream response body must never reach the client (JEBV4-250)");
        raw.Should().NotContain("NullReferenceException");
        raw.Should().NotContain("The HTTP status code of the response was not expected",
            "the NSwag ApiException.Message wrapper must not be echoed either");
        raw.Should().StartWith("{",
            "the error body must be a JSON ProblemDetails envelope, not a bare string");
    }

    [Fact]
    public async Task Health_UpstreamNotFound_Preserves_Status_And_Does_Not_Leak_Body()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Notification/health");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);
        problem.Title.Should().Be("The notification request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_n250",
            "the upstream response body must never reach the client, even on a 404");
    }

    [Fact]
    public async Task Health_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302
            {
                Content = new StringContent(Canary, Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithNotificationStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Notification/health");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502, never forwarded");
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("The notification request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_n250");
    }

    /// <summary>
    /// Source-scan regression guard (same grep-guard idiom as
    /// <see cref="ChatControllerErrorShapeTests"/>): pins the whole file to zero LIVE
    /// occurrences of the leaky <c>StatusCode(ex.StatusCode, ex.Message)</c>
    /// passthrough, and every <c>catch (NotificationApiException</c> paired 1:1 with
    /// an <c>UpstreamProblem(ex)</c> call. Comment lines are excluded so a helper's own
    /// XML doc citing the retired pattern does not count.
    /// </summary>
    [Fact]
    public void NotificationController_Source_Has_No_Live_Upstream_Passthrough_And_All_Catches_Are_Sanitized()
    {
        var path = LocateControllerSource("NotificationController.cs");
        path.Should().NotBeNull(
            "src/JeebGateway/Controllers/NotificationController.cs must be locatable from the test bin dir");

        var liveCode = string.Join(
            "\n",
            File.ReadAllLines(path!).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        CountOccurrences(liveCode, "StatusCode(ex.StatusCode").Should().Be(0,
            "JEBV4-250: no catch may return the upstream status with a raw ex.Message/body payload — "
            + "every upstream failure must route through UpstreamProblem");

        var catches = CountOccurrences(liveCode, "catch (NotificationApiException");
        var sanitized = CountOccurrences(liveCode, "UpstreamProblem(ex)");

        catches.Should().BeGreaterThan(0,
            "the guard must actually see the upstream catch sites (an emptied/renamed file must not vacuously pass)");
        sanitized.Should().Be(catches,
            "every catch (NotificationApiException ...) must return UpstreamProblem(ex) — a single-site revert breaks this pairing");
    }

    private static int CountOccurrences(string haystack, string needle)
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

    private static string? LocateControllerSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway", "Controllers", fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
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

    private static HttpClient MintBearerClient(WebApplicationFactory<Program> factory)
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
            claims: new[] { new Claim("sub", "notif-jebv4-250"), new Claim("roles", "client") },
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
