using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S06 keystone — gateway presence thin-wire contract
/// (DELIVERY-SERVICE-RELOCATION-DESIGN.md §8).
///
/// <para>
/// These tests exercise the on-the-wire DELEGATION contract: the gateway
/// availability + location controllers forward to the canonical delivery-service
/// presence routes and map the snake_case Go response back. They run the real
/// <see cref="DeliveryServiceClient"/> against a stub HTTP handler (the same
/// boundary pattern as <c>MatchingEndpointTests</c>) so the assertions cover the
/// actual HTTP path, body field names, and response binding — not an in-process
/// fake — which is what locks the contract the REPO 1 (Go) build must honour.
/// </para>
/// </summary>
public class PresenceThinWireTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // -------- PATCH /jeebers/me/availability (online) --------------------------

    [Fact]
    public async Task Patch_Online_Posts_To_Canonical_Availability_Route_With_SnakeCase_Body()
    {
        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            if (req.Content is not null) bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"jeeber_id":"jeeber-1","online":true,"vehicle_type":"motorbike",
                 "zone":"amman-downtown","lat":31.9539,"lng":35.9106,
                 "last_seen_at":"2026-05-15T12:00:00Z","updated_at":"2026-05-15T12:00:00Z"}
                """);
        });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-1");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "motorbike",
            zone = "amman-downtown",
            latitude = 31.9539,
            longitude = 35.9106
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The online write hit the canonical delivery-service presence route with
        // the jeeber id in the PATH.
        var setReq = captured.Single(r =>
            r.Method == HttpMethod.Post &&
            r.RequestUri!.AbsolutePath == "/api/v1/jeebers/jeeber-1/availability");
        setReq.Should().NotBeNull();

        var sent = bodies.Single();
        sent.Should().Contain("\"online\":true");
        sent.Should().Contain("\"vehicle_type\":\"motorbike\"");   // snake_case (Go)
        sent.Should().Contain("\"zone\":\"amman-downtown\"");
        sent.Should().Contain("\"lat\":31.9539");
        sent.Should().Contain("\"lng\":35.9106");
        sent.Should().NotContain("vehicleType");                   // no camelCase leakage

        // The snake_case Go response binds onto the public response shape.
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.UserId.Should().Be("jeeber-1");
        body.Online.Should().BeTrue();
        body.VehicleType.Should().Be("motorbike");
        body.Zone.Should().Be("amman-downtown");
        body.Latitude.Should().Be(31.9539);
        body.Longitude.Should().Be(35.9106);
    }

    // -------- PATCH /jeebers/me/availability (offline, N13) --------------------

    [Fact]
    public async Task Patch_Offline_Posts_Offline_To_Upstream_And_Returns_200()
    {
        // N13 fix: the offline path writes through to delivery-service and returns
        // 200 with an offline body — it does not 500.
        var bodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            if (req.Content is not null) bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"jeeber_id":"jeeber-off","online":false,
                 "updated_at":"2026-05-15T12:05:00Z"}
                """);
        });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-off");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        bodies.Single().Should().Contain("\"online\":false");

        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.Online.Should().BeFalse();
    }

    // -------- GET /jeebers/me/availability ------------------------------------

    [Fact]
    public async Task Get_Reads_From_Canonical_Route_And_Maps_Response()
    {
        var captured = new List<HttpRequestMessage>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(
                """
                {"jeeber_id":"jeeber-g","online":true,"vehicle_type":"car",
                 "zone":"amman-shmeisani","lat":31.96,"lng":35.90,
                 "updated_at":"2026-05-15T12:00:00Z"}
                """);
        });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-g");

        var resp = await client.GetAsync("/jeebers/me/availability");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().Contain(r =>
            r.Method == HttpMethod.Get &&
            r.RequestUri!.AbsolutePath == "/api/v1/jeebers/jeeber-g/availability");

        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.Online.Should().BeTrue();
        body.VehicleType.Should().Be("car");
        body.Zone.Should().Be("amman-shmeisani");
    }

    [Fact]
    public async Task Get_When_Never_Online_Returns_Offline_Default_Not_500()
    {
        // Upstream 404 (no presence row) → offline default, never a 500.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json")
            });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-new");

        var resp = await client.GetAsync("/jeebers/me/availability");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.UserId.Should().Be("jeeber-new");
        body.Online.Should().BeFalse();
        body.Zone.Should().BeNull();
    }

    // -------- POST /location/update → heartbeat (A2) --------------------------

    [Fact]
    public async Task Location_Update_Sends_Latest_Fix_As_Heartbeat_To_Delivery()
    {
        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            if (req.Content is not null) bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"jeeber_id":"jeeber-hb","online":true,"lat":24.7120,"lng":46.6720,
                 "updated_at":"2026-05-15T12:00:00Z"}
                """);
        });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-hb");

        var now = DateTimeOffset.UtcNow;
        var resp = await client.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.7100, lng = 46.6700, accuracy = 12.5, timestamp = now.AddSeconds(-10) },
                new { lat = 24.7120, lng = 46.6720, accuracy = 6.5,  timestamp = now },
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The heartbeat hit the canonical route with the device-latest fix.
        var hb = captured.Single(r =>
            r.Method == HttpMethod.Post &&
            r.RequestUri!.AbsolutePath == "/api/v1/jeebers/jeeber-hb/heartbeat");
        hb.Should().NotBeNull();

        var sent = bodies.Single();
        sent.Should().Contain("\"lat\":24.712");
        sent.Should().Contain("\"lng\":46.672");

        // The public response shape is unchanged.
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOpts);
        body!.Accepted.Should().Be(2);
        body.Rejected.Should().Be(0);
        body.Latest!.Lat.Should().Be(24.7120);
    }

    [Fact]
    public async Task Location_Update_Heartbeat_404_Does_Not_500_The_Ingest()
    {
        // A heartbeat for a jeeber who never went online is a 404 upstream; the
        // GPS ingest must still succeed (the fix is retained locally for SSE).
        var stub = new StubHttpMessageHandler(_ =>
            JsonResponse("""{"reason":"not_online"}""", HttpStatusCode.NotFound));

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-404");

        var resp = await client.PostAsJsonAsync("/location/update", new
        {
            points = new[] { new { lat = 24.0, lng = 46.0, accuracy = (double?)null, timestamp = DateTimeOffset.UtcNow } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOpts);
        body!.Accepted.Should().Be(1);
    }

    [Fact]
    public async Task Location_Update_All_Rejected_Sends_No_Heartbeat()
    {
        // When every point is out-of-range there is no latest fix → no heartbeat.
        var captured = new List<HttpRequestMessage>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("{}");
        });

        using var factory = NewFactory(s => ReplaceDeliveryClient(s, stub));
        var client = JeeberClient(factory, "jeeber-rej");

        var resp = await client.PostAsJsonAsync("/location/update", new
        {
            points = new[] { new { lat = 200.0, lng = 46.0, accuracy = (double?)null, timestamp = DateTimeOffset.UtcNow } }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().BeEmpty("no accepted fix means no presence heartbeat");
    }

    // -------------------------------------------------------------------------
    // Helpers (mirror MatchingEndpointTests' boundary-stub pattern)
    // -------------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(Action<IServiceCollection>? configure = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configure is not null) builder.ConfigureTestServices(configure);
        });

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Dual-role edge: satisfies both availability.toggle ({jeeber}) and
        // delivery.gps.stream ({jeeber}) capabilities.
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static void ReplaceDeliveryClient(IServiceCollection services, HttpMessageHandler handler)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upstream-delivery.test") };
        services.AddSingleton<IDeliveryServiceClient>(new DeliveryServiceClient(http));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
