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
/// ADR-004 (upgrade-not-switch / gateway-only session audience) auth invariants.
/// SUPERSEDES the ADR-003 trust invariant this fixture used to encode (a UM-re-issued
/// <c>aud=user-management</c> token authorizing a client route): there is no role-switch
/// ceremony, so a client-route session token has exactly ONE valid audience —
/// <c>aud=jeeb-clients</c>, gateway-issued. A token with <c>aud=user-management</c> on a
/// client route is now REJECTED (401). This closes the E4b/N5/N7.3 contradiction.
///
/// These tests drive the full HTTP pipeline through <see cref="WebApplicationFactory{TEntry}"/>
/// against the real <c>[Authorize]</c> client routes, with the upstream client stubbed so
/// authorization is the only variable. Invariants asserted:
/// <list type="bullet">
///   <item><description>UM-audience token → <b>401</b> on BOTH <c>/v1/users/me</c> AND
///     <c>/api/UserPreferences/preferences</c> (the default policy is GatewayBearerScheme-only).</description></item>
///   <item><description>Gateway-issued (<c>aud=jeeb-clients</c>) token → <b>200</b> (zero regression).</description></item>
///   <item><description>UM-claimed token signed with the WRONG key → 401 (signature still validates;
///     no blind accept on the dormant UM scheme).</description></item>
///   <item><description>Unknown issuer even with a valid key → 401 (issuer pinning).</description></item>
/// </list>
/// The test host uses the gateway's <c>Jwt:SigningKey</c> (from appsettings); the wrong-key case
/// signs with a distinct key to prove signature validation runs even on the rejected UM path.
/// </summary>
public class UmIssuerTokenTrustTests
{
    private const string TestUserId = "e666b167-8160-4b6f-8dde-1c08a016559e";
    private const string UmIssuer = "user-management";
    private const string UmAudience = "user-management";

    // -----------------------------------------------------------------
    // REJECT (E4b/N5/N7.3): a UM-AUDIENCE token, even correctly signed, is
    // 401 on client routes — the default policy is GatewayBearerScheme-only.
    // Asserted on TWO distinct [Authorize] client routes.
    // -----------------------------------------------------------------
    [Theory]
    [InlineData("/v1/users/me")]
    [InlineData("/api/UserPreferences/preferences")]
    public async Task UmAudienceToken_IsRejected_401(string route)
    {
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called for a UM-audience token on a client route"));

        using var factory = NewFactory(stub);
        var client = factory.CreateClient();
        // Correctly signed with the trusted key — rejection is on AUDIENCE/policy, not signature.
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintUmToken(factory, signWithGatewayKey: true));

        var resp = await client.GetAsync(route);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"ADR-004: a token with aud=user-management must be rejected (401) on the client route {route} — "
            + "the default authorization policy accepts only the gateway-issued aud=jeeb-clients scheme (E4b/N5/N7.3)");
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
                // ADR-005 §B: the real ADR-004 mint (TokenService.BuildAccessToken) emits the PLURAL
                // "roles" claim per role, and UserIdentity.GetRoles / the bearer RoleClaimType read
                // "roles" (NOT the singular "role"). Carry it so this gateway-issued token authorizes
                // the now L2-gated /api/UserPreferences/preferences (notification.prefs.self). The
                // singular "role"/"active_role" stay for any active-role display assertions.
                new Claim("roles", "jeeber"),
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
