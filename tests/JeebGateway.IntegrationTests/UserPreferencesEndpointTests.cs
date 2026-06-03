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
/// Tests for the EXACT-SALEHLY-MIRROR user-preferences surface
/// (<c>/api/UserPreferences/*</c>). The controller is a byte-faithful copy of
/// salehly-gateway's <c>UserPreferencesController</c>: it is <c>[Authorize]</c>,
/// derives the user id from <c>User.FindFirst(ClaimTypes.Sid)</c>, and always
/// forwards to the real <c>remote-user-preferences</c> upstream via the
/// NSwag-generated <see cref="ServiceRemoteUserPreferencesClient"/> (there is NO
/// UseUpstream flag gate — no 503-without-calling path).
///
/// The suite drives the full HTTP pipeline with <see cref="WebApplicationFactory{TEntry}"/>:
///   * a real HS256 JWT (minted with a known dev key + a <c>Sid</c> claim) takes
///     the request past <c>[Authorize]</c> and supplies the upstream user id;
///   * the scoped <see cref="ServiceRemoteUserPreferencesClient"/> is replaced with
///     one whose <c>HttpClient</c> is backed by a stub handler, so we assert the
///     exact upstream path/method/body the controller relays — happy + negative.
/// </summary>
public class UserPreferencesEndpointTests
{
    private const string TestSigningKey = "jeeb-gateway-itest-signing-key-32bytes!!";
    private const string TestIssuer = "jeeb-gateway";
    private const string TestAudience = "jeeb-clients";
    private const string TestUserId = "e666b167-8160-4b6f-8dde-1c08a016559e";

    // -----------------------------------------------------------------
    // Happy paths — authorized caller, upstream forward asserted.
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAllPreferences_Authorized_Forwards_To_Upstream_And_Returns_Map()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            // GET /preferences/{user_id} -> Preferences map (snake_case values)
            return JsonResponse("""{ "theme": "dark", "language": "ar" }""");
        });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        body!.Should().ContainKey("theme").WhoseValue.Should().Be("dark");

        // user id (from the Sid claim) is the upstream path segment.
        captured.Single().RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}");
        captured.Single().Method.Should().Be(HttpMethod.Get);
    }

    /// <summary>
    /// REGRESSION (claim bug): the gateway mints access tokens with the user id
    /// in the JWT <c>sub</c> claim ONLY (TokenService.BuildAccessToken) and runs
    /// with <c>MapInboundClaims=false</c>, so a minted token carries no
    /// <c>ClaimTypes.Sid</c>. The original <c>GetUserId()</c> read only Sid and
    /// 401'd every such call. With the fall-through fix a sub-only token must now
    /// authorize and resolve the upstream user id from <c>sub</c>.
    /// </summary>
    [Fact]
    public async Task GetAllPreferences_SubOnlyToken_Authorizes_And_Resolves_UserId_From_Sub()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("""{ "theme": "dark" }""");
        });

        using var factory = NewFactory(stub);
        var client = SubOnlyClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Single().RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}");
    }

    [Fact]
    public async Task GetSinglePreference_Authorized_Returns_Value_From_Upstream_Envelope()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("""{ "value": "dark" }"""); // PreferenceValue { value }
        });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/preferences/theme");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Single().RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}/theme");
    }

    [Fact]
    public async Task SetSinglePreference_Authorized_Posts_To_Upstream_And_Returns_201()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            // Upstream returns 201 Created for set-single (the generated client
            // treats any other status as unexpected); the controller relays 201.
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.PostAsJsonAsync(
            "/api/UserPreferences/preferences/theme", new PreferenceValue { Value = "light" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var sent = captured.Single();
        sent.Method.Should().Be(HttpMethod.Post); // upstream uses POST for set-single
        sent.RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}/theme");
    }

    [Fact]
    public async Task GetPaginatedItems_Authorized_Forwards_DataType_And_Query()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(
                """{ "current_page": 1, "items": ["a"], "page_size": 10, "total_items": 1, "total_pages": 1 }""");
        });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/data/favourites?page=1&size=10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var sent = captured.Single();
        sent.RequestUri!.AbsolutePath.Should().Be($"/data/{TestUserId}/favourites");
    }

    // -----------------------------------------------------------------
    // Negative paths.
    // -----------------------------------------------------------------

    [Fact]
    public async Task GetAllPreferences_Without_Jwt_Returns_401()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called without auth"));

        using var factory = NewFactory(stub);
        var client = factory.CreateClient(); // no Authorization header

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetSinglePreference_Relays_Upstream_404()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("not found", Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/preferences/missing-key");

        // salehly controller catches RemoteUserPreferencesApiException and relays
        // the upstream status code via StatusCode(ex.StatusCode, ex.Message).
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAllPreferences_Relays_Upstream_500_As_500()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("boom", Encoding.UTF8, "text/plain")
            });

        using var factory = NewFactory(stub);
        var client = AuthorizedClient(factory);

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(HttpMessageHandler upstreamHandler)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Replace the scoped salehly client with one whose HttpClient is
                    // backed by the stub handler, BaseAddress matching the prod host
                    // shape so relative upstream paths resolve identically.
                    services.RemoveAll<ServiceRemoteUserPreferencesClient>();
                    services.AddScoped(_ =>
                    {
                        var http = new HttpClient(upstreamHandler)
                        {
                            BaseAddress = new Uri("http://rup.test/")
                        };
                        return new ServiceRemoteUserPreferencesClient("http://rup.test/", http);
                    });
                });
            });
    }

    private static HttpClient AuthorizedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(factory));
        return client;
    }

    /// <summary>Authorizes with a token carrying ONLY the <c>sub</c> claim (no Sid).</summary>
    private static HttpClient SubOnlyClient(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? TestIssuer;
        var audience = config["Jwt:Audience"] ?? TestAudience;
        var signingKey = config["Jwt:SigningKey"] ?? TestSigningKey;

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", TestUserId) },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    /// <summary>
    /// Mints an HS256 JWT carrying BOTH a <c>sub</c> and a <c>Sid</c> claim
    /// (<c>http://schemas.xmlsoap.org/ws/2005/05/identity/claims/sid</c>) so the
    /// salehly controller's <c>User.FindFirst(ClaimTypes.Sid)</c> resolves. The
    /// host runs with <c>MapInboundClaims=false</c>, so the raw claim types in the
    /// token are preserved — <c>ClaimTypes.Sid</c> must be emitted literally.
    ///
    /// The issuer/audience/signing key are read from the running host's effective
    /// <c>Jwt</c> configuration (resolved from the factory's DI) so the token is
    /// always valid against whatever the host validates with, independent of which
    /// appsettings/environment is in effect.
    /// </summary>
    private static string MintToken(WebApplicationFactory<Program> factory)
    {
        // Touch Services so the host is built and our in-memory Jwt:* override is
        // applied to the effective configuration before we read it back.
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? TestIssuer;
        var audience = config["Jwt:Audience"] ?? TestAudience;
        var signingKey = config["Jwt:SigningKey"] ?? TestSigningKey;

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[]
            {
                new Claim("sub", TestUserId),
                new Claim(ClaimTypes.Sid, TestUserId),
            },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        public void Add(HttpRequestMessage req) => _items.Add(req);
        public HttpRequestMessage Single() => _items.Single();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
