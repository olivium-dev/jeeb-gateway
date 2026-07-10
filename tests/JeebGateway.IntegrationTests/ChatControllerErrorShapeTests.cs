using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.service.ServiceChat;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-242 (info-leak): ChatController used to carry 32 bare
/// <c>return StatusCode(ex.StatusCode, ex.Message)</c> catches. The NSwag
/// <see cref="ApiException"/>.Message embeds up to 512 chars of the raw upstream
/// chat-service response body, so every chat endpoint leaked upstream exception
/// detail to the caller. The fix routes every catch through
/// <c>ChatController.UpstreamProblem</c>, which returns a sanitized RFC 7807
/// ProblemDetails: the upstream status is preserved, but the upstream
/// message/body is logged server-side only and never put on the wire.
///
/// <para>Mirrors <see cref="UserControllerErrorShapeTests"/> (the JEBV4-63
/// precedent). The public <c>/api/Chat/health</c> and <c>/api/Chat/check</c>
/// passthroughs exercise the exact <c>catch (ApiException) → UpstreamProblem</c>
/// path shared by all 32 chat actions, so a raw HttpClient stub bound directly to
/// the scoped <see cref="ServiceChatClient"/> deterministically drives the leak
/// path without needing a bearer.</para>
/// </summary>
public class ChatControllerErrorShapeTests
{
    // The kind of internal exception text an upstream service must never leak to a
    // client. Present in the stub's response body, it lands in ApiException.Message
    // (which the pre-fix catch returned verbatim) — so asserting it is ABSENT from
    // the wire response is the info-leak regression lock.
    private const string Canary =
        "System.NullReferenceException: SECRET_CANARY_ce8f at ChatService.Internal.SecretRepo.Load() line 42";

    [Fact]
    public async Task Health_UpstreamServerError_Is_Sanitized_ProblemDetails_Not_Leaked_Message()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent(Canary, System.Text.Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithChatStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Chat/health");

        // Upstream status is preserved...
        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);

        // ...the body is an RFC 7807 ProblemDetails envelope with a GENERIC title...
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.InternalServerError);
        problem.Title.Should().Be("The chat request could not be completed.");

        // ...and NONE of the upstream exception detail is echoed to the caller.
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_ce8f",
            "the upstream response body must never reach the client (JEBV4-242)");
        raw.Should().NotContain("NullReferenceException");
        raw.Should().NotContain("The HTTP status code of the response was not expected",
            "the NSwag ApiException.Message wrapper must not be echoed either");
        raw.Should().StartWith("{",
            "the error body must be a JSON ProblemDetails envelope, not a bare string");
    }

    [Fact]
    public async Task Check_UpstreamNotFound_Preserves_Status_And_Does_Not_Leak_Body()
    {
        // A non-5xx upstream failure (404) must ALSO be forwarded with its status
        // preserved and no upstream body leaked.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(Canary, System.Text.Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithChatStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Chat/check");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.NotFound);
        problem.Title.Should().Be("The chat request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_ce8f",
            "the upstream response body must never reach the client, even on a 404");
    }

    [Fact]
    public async Task Health_Upstream_Status_Outside_Error_Range_Is_Clamped_To_502()
    {
        // A 3xx from chat-service (e.g. a stray redirect) is still a failed upstream
        // call: NSwag throws ApiException(302) because the status is outside 2xx.
        // UpstreamProblem must NOT forward a non-error status to the caller — the
        // clamp branch maps anything outside [400,600) to 502 Bad Gateway.
        // (The stub handler is the pipeline terminal, so nothing auto-follows the 302.)
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Found) // 302
            {
                Content = new StringContent(Canary, System.Text.Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithChatStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Chat/health");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway,
            "an upstream status outside [400,600) must be clamped to 502, never forwarded");
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("The chat request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_ce8f",
            "the upstream response body must never reach the client on the clamp branch either");
    }

    [Fact]
    public async Task Health_Upstream_401_Is_Preserved_Unchanged_Without_Leaking_Body()
    {
        // Auth semantics must survive sanitization: an upstream 401 stays a 401 on the
        // wire. The caller's bearer authenticated at the GATEWAY, so the only source of
        // this 401 is the action's sanitized upstream mapping — pinned by the generic
        // title below, which a bare middleware challenge would not carry.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent(Canary, System.Text.Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactoryWithChatStub(stub);
        var client = MintBearerClient(factory);

        var resp = await client.GetAsync("/api/Chat/health");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an upstream 401 is a valid client-facing auth signal and must be preserved unchanged");
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.Unauthorized);
        problem.Title.Should().Be("The chat request could not be completed.");
        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("SECRET_CANARY_ce8f",
            "the upstream 401 body must not be echoed to the caller");
    }

    /// <summary>
    /// Source-scan regression guard (same grep-guard idiom as
    /// <see cref="DeliveryStateMachineRetiredGuardTests"/>): a future edit that
    /// reverts ONE of the 32 catch sites back to the leaky
    /// <c>StatusCode(ex.StatusCode, ex.Message)</c> passthrough would still pass the
    /// behavioral tests above (they drive health/check only), so this guard pins the
    /// whole file: zero LIVE occurrences of the leaky passthrough, and every
    /// <c>catch (ChatApiException</c> paired 1:1 with a <c>UpstreamProblem(ex)</c>
    /// call. Comment lines are excluded so the helper's own XML doc (which cites the
    /// retired pattern as history) does not count.
    /// </summary>
    [Fact]
    public void ChatController_Source_Has_No_Live_Upstream_Passthrough_And_All_Catches_Are_Sanitized()
    {
        var path = LocateChatControllerSource();
        path.Should().NotBeNull(
            "src/JeebGateway/Controllers/ChatController.cs must be locatable from the test bin dir");

        // Strip whole-line comments (covers /// XML docs and // remarks). The file
        // carries no /* */ block comments; trailing comments never contain the
        // scanned tokens, and stripping after an inline "//" would corrupt
        // "https://" string literals — so line-level filtering is the safe scan.
        var liveCode = string.Join(
            "\n",
            File.ReadAllLines(path!).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        CountOccurrences(liveCode, "StatusCode(ex.StatusCode").Should().Be(0,
            "JEBV4-242: no catch may return the upstream status with a raw ex.Message/body payload — "
            + "every upstream failure must route through UpstreamProblem");

        var catches = CountOccurrences(liveCode, "catch (ChatApiException");
        var sanitized = CountOccurrences(liveCode, "UpstreamProblem(ex)");

        catches.Should().BeGreaterThan(0,
            "the guard must actually see the upstream catch sites (an emptied/renamed file must not vacuously pass)");
        sanitized.Should().Be(catches,
            "every catch (ChatApiException ...) must return UpstreamProblem(ex) — a single-site revert breaks this pairing");
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

    private static string? LocateChatControllerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "JeebGateway", "Controllers", "ChatController.cs");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static WebApplicationFactory<Program> NewFactoryWithChatStub(HttpMessageHandler stub)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                // Replace the scoped NSwag ServiceChatClient with one bound to the stub
                // handler (bypasses the named-client resilience pipeline, exactly like
                // UserControllerErrorShapeTests), so the upstream failure is immediate
                // and deterministic — no retry/backoff.
                services.RemoveAll<ServiceChatClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://chat.test/") };
                    return new ServiceChatClient("http://chat.test/", http);
                });
            });
        });

    /// <summary>
    /// Mints an HS256 bearer against the host's effective Jwt config (same pattern as
    /// ErrorShapeNormalizationTests / UserPreferencesEndpointTests). The chat health/check
    /// passthroughs carry an L1 [Authorize] fallback so they need an authenticated
    /// principal — but [PublicEndpoint] opts them out of the capability layer, so any
    /// identified caller reaches the action (and thus the upstream call under test).
    /// </summary>
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
            claims: new[] { new Claim("sub", "chat-jebv4-242"), new Claim("roles", "client") },
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
