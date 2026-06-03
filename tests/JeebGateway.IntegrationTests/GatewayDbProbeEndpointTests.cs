using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Drives the additive, read-only DB-probe pass-through surface
/// (<see cref="JeebGateway.Controllers.GatewayDbProbeController"/>) through the
/// full HTTP pipeline with <see cref="WebApplicationFactory{TEntry}"/>. Each
/// route is asserted on:
///   * a NEGATIVE path — no bearer token ⇒ 401 (every route is [Authorize]);
///   * a HAPPY path — an authorized caller's GET is relayed verbatim to the
///     named <c>db-probe-*</c> upstream HttpClient (stubbed), and the upstream
///     status + JSON body are returned unchanged.
///
/// The upstream is stubbed by overriding the named client's primary handler, so
/// these tests assert the gateway's relay contract without standing up the real
/// Mongo / Postgres / Redis backends. The probe BaseUrl keys are supplied via an
/// in-memory configuration override so the controller's "configured?" guard
/// passes under the test environment (whose appsettings omit them).
/// </summary>
public class GatewayDbProbeEndpointTests
{
    private const string TestSigningKey = "jeeb-gateway-itest-signing-key-32bytes!!";
    private const string TestUserId = "e3d465d6-ab05-49e1-ae41-0879d0bc35d9";

    public static IEnumerable<object[]> ProbeRoutes() => new[]
    {
        new object[] { "/api/notification/notifications?receiver=" + TestUserId, "db-probe-notification" },
        new object[] { $"/locations/user/{TestUserId}", "db-probe-geolocation" },
        new object[] { "/api/v1/payments/cod_jeeb/by-delivery/dlv-123", "db-probe-unified-payment" },
        new object[] { $"/api/compliments/list?userId={TestUserId}", "db-probe-compliment" },
        new object[] { $"/api/ban/{TestUserId}/status", "db-probe-ban" },
        new object[] { "/api/otp/status/+96512345678", "db-probe-otp" },
    };

    [Theory]
    [MemberData(nameof(ProbeRoutes))]
    public async Task ProbeRoute_WithoutToken_Returns401(string route, string _)
    {
        using var factory = NewFactory((__, ___) => JsonResponse("""{"ok":true}"""));
        var client = factory.CreateClient();

        var resp = await client.GetAsync(route);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [MemberData(nameof(ProbeRoutes))]
    public async Task ProbeRoute_Authorized_RelaysUpstreamBodyVerbatim(string route, string clientName)
    {
        var hit = false;
        using var factory = NewFactory((name, req) =>
        {
            if (name == clientName) hit = true;
            return JsonResponse("""{"items":[],"total":0}""");
        });
        var client = Authorized(factory);

        var resp = await client.GetAsync(route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        hit.Should().BeTrue($"the {clientName} upstream client should have been dialed");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"total\":0");
    }

    [Fact]
    public async Task Realtime_AdminTopics_WithToken_RelaysUpstream()
    {
        var hit = false;
        using var factory = NewFactory((name, req) =>
        {
            if (name == "db-probe-realtime") hit = true;
            return JsonResponse("""{"topics":["jeeb:chat"]}""");
        });
        var client = Authorized(factory);

        var resp = await client.GetAsync("/realtime/admin/topics");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        hit.Should().BeTrue();
    }

    [Fact]
    public async Task Realtime_AdminTopics_WithoutToken_Returns401()
    {
        using var factory = NewFactory((_, __) => JsonResponse("{}"));
        var resp = await factory.CreateClient().GetAsync("/realtime/admin/topics");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        Func<string, HttpRequestMessage, HttpResponseMessage> upstream)
    {
        var probeConfig = new Dictionary<string, string?>
        {
            ["ServiceNotificationClient:BaseUrl"] = "http://notif.test/",
            ["Services:Geolocation:BaseUrl"] = "http://geo.test/",
            ["Services:UnifiedPayment:BaseUrl"] = "http://pay.test/",
            ["Services:Realtime:BaseUrl"] = "http://realtime.test/",
            ["Services:Compliment:BaseUrl"] = "http://compliment.test/",
            ["Services:Ban:BaseUrl"] = "http://ban.test/",
            ["Services:ServiceOTP:BaseUrl"] = "http://otp.test/",
            // Gate the realtime admin-topics route open for the relay test.
            ["FeatureFlags:UseUpstream:Realtime"] = "true",
        };

        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(probeConfig));
                builder.ConfigureTestServices(services =>
                {
                    foreach (var name in new[]
                    {
                        "db-probe-notification", "db-probe-geolocation", "db-probe-unified-payment",
                        "db-probe-realtime", "db-probe-compliment", "db-probe-ban", "db-probe-otp",
                    })
                    {
                        var captured = name;
                        services.AddHttpClient(captured)
                            .ConfigurePrimaryHttpMessageHandler(() =>
                                new StubHandler((req) => upstream(captured, req)));
                    }
                });
            });
    }

    private static HttpClient Authorized(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(factory));
        return client;
    }

    private static string MintToken(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? TestSigningKey;

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);

        // Only `sub` — exactly what the gateway mints. No Sid. This also exercises
        // the claim resolution the DB-probe routes rely on for [Authorize].
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", TestUserId) },
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

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
