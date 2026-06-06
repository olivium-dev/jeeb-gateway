using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
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
/// H-B5 (S02 ADR-001-rev3 token-authority): after a role switch user-management
/// re-issues the caller's JWT with <c>iss=user-management</c> / <c>aud=user-management</c>.
/// The gateway must ACCEPT and FULLY VALIDATE that token on protected routes,
/// ALONGSIDE its own <c>iss=jeeb-gateway</c> tokens — and must REJECT anything
/// signed with the wrong key (no blind accept; key-confusion closed).
///
/// The auth wiring is a two-scheme, issuer-routed design (NOT a widened
/// ValidIssuers/multi-key single scheme). These tests drive the full HTTP pipeline
/// through <see cref="WebApplicationFactory{TEntry}"/> against the real
/// <c>[Authorize]</c> <c>/api/UserPreferences/preferences</c> route, with the
/// upstream client stubbed so authorization is the only variable.
///
/// Both issuers are trusted with the SAME effective HS256 key in the test host:
/// the gateway's <c>Jwt:SigningKey</c> (from appsettings), and UM trust
/// (<c>UmJwt:SigningKey</c> empty) falls back to it — mirroring the live fleet
/// where UM and the gateway share one secret. The wrong-key case signs with a
/// distinct key to prove signature validation actually runs on the UM path.
/// </summary>
public class UmIssuerTokenTrustTests
{
    private const string TestUserId = "e666b167-8160-4b6f-8dde-1c08a016559e";
    private const string UmIssuer = "user-management";
    private const string UmAudience = "user-management";

    // -----------------------------------------------------------------
    // ACCEPT: a real UM-re-issued token authorizes a protected route.
    // -----------------------------------------------------------------
    [Fact]
    public async Task UmIssuedToken_Authorizes_Protected_Route_And_Forwards_Upstream()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("""{ "theme": "dark" }""");
        });

        using var factory = NewFactory(stub);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintUmToken(factory, signWithGatewayKey: true));

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "a UM-re-issued token (iss=user-management) must authorize H-B5 protected routes");
        // user id resolves from the UM token's Sid claim into the upstream path.
        captured.Single().RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}");
    }

    // -----------------------------------------------------------------
    // ZERO REGRESSION: a gateway-issued token still authorizes.
    // -----------------------------------------------------------------
    [Fact]
    public async Task GatewayIssuedToken_Still_Authorizes_Protected_Route()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("""{ "theme": "light" }""");
        });

        using var factory = NewFactory(stub);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintGatewayToken(factory));

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "existing jeeb-gateway-issued tokens must keep authorizing (H-A3/H-B3 spine)");
        captured.Single().RequestUri!.AbsolutePath.Should().Be($"/preferences/{TestUserId}");
    }

    // -----------------------------------------------------------------
    // REJECT: a UM-claimed token signed with the WRONG key is 401.
    // -----------------------------------------------------------------
    [Fact]
    public async Task UmIssuerToken_SignedWithWrongKey_Is_Rejected_401()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called for a forged token"));

        using var factory = NewFactory(stub);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintUmToken(factory, signWithGatewayKey: false));

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a token claiming iss=user-management but signed with a key the gateway does not trust must be rejected — signature validation runs on the UM path, no blind accept");
    }

    // -----------------------------------------------------------------
    // REJECT: an unknown issuer signed with the gateway key is 401.
    // (issuer pinning — neither scheme's ValidIssuer matches.)
    // -----------------------------------------------------------------
    [Fact]
    public async Task UnknownIssuerToken_EvenWithValidKey_Is_Rejected_401()
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called for an untrusted issuer"));

        using var factory = NewFactory(stub);
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var gatewayKey = config["Jwt:SigningKey"]!;

        var token = BuildToken(
            issuer: "some-other-service",
            audience: "jeeb-clients",
            signingKey: gatewayKey);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/api/UserPreferences/preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "an unrecognized issuer must not be trusted even when signed with a key the gateway holds (issuer pinning)");
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

    /// <summary>
    /// Mints a UM-style token (iss=user-management / aud=user-management) carrying
    /// sub + Sid. When <paramref name="signWithGatewayKey"/> is true it signs with
    /// the host's effective Jwt:SigningKey (which the UM scheme trusts via fallback);
    /// when false it signs with a deliberately different key to exercise rejection.
    /// </summary>
    private static string MintUmToken(WebApplicationFactory<Program> factory, bool signWithGatewayKey)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var trustedKey = config["Jwt:SigningKey"]!;
        var key = signWithGatewayKey
            ? trustedKey
            : "an-entirely-different-untrusted-key-32bytes!!";

        return BuildToken(UmIssuer, UmAudience, key);
    }

    private static string MintGatewayToken(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"]!;
        var audience = config["Jwt:Audience"]!;
        var signingKey = config["Jwt:SigningKey"]!;
        return BuildToken(issuer, audience, signingKey);
    }

    private static string BuildToken(string issuer, string audience, string signingKey)
    {
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
                new Claim("active_role", "jeeber"),
                new Claim("role", "jeeber"),
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
