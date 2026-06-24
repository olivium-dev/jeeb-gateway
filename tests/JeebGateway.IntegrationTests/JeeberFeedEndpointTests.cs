using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Matching;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Jeeber pull feed — <c>GET /jeebers/me/feed</c> (the inverse of
/// <c>/matching/run</c>). The gateway is a thin BFF: it resolves the caller and
/// forwards to the canonical delivery-service route
/// <c>GET /api/v1/jeebers/{id}/feed</c> via <see cref="IDeliveryServiceClient"/>,
/// mapping the snake_case Go result onto the gateway DTO.
///
/// These tests exercise the DELEGATION contract — the request is forwarded to the
/// right upstream route with the caller's id, the snake_case result is mapped,
/// the tier CODE is surfaced as $.tierId, the limit is forwarded, a 404 degrades
/// to an empty feed, and the jeeber-only capability gate holds — NOT the feed
/// pipeline internals (GPS resolution, radius cut, ordering), which live in
/// delivery-service's own suite.
/// </summary>
public class JeeberFeedEndpointTests
{
    [Fact]
    public async Task Feed_Forwards_To_Delivery_Upstream_And_Maps_Result()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(
                """
                {
                  "jeeber_id":"jeeber-x",
                  "items":[
                    {"request_id":"del_1","tier_id":"uuid-flash","tier_code":"flash",
                     "pickup_lat":24.71,"pickup_lng":46.67,"distance_km":1.2,
                     "created_at":"2026-06-20T12:00:00Z"}
                  ],
                  "count":1
                }
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "jeeber-x");
        var resp = await client.GetAsync("/jeebers/me/feed?limit=10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JeeberFeedResponse>(JsonOpts);
        body!.JeeberId.Should().Be("jeeber-x");
        body.Count.Should().Be(1);
        body.Items.Should().ContainSingle();
        var item = body.Items[0];
        item.RequestId.Should().Be("del_1");
        // $.tierId carries the human-readable tier CODE, not the UUID.
        item.TierId.Should().Be("flash");
        item.PickupLat.Should().Be(24.71);
        item.PickupLng.Should().Be(46.67);
        item.DistanceKm.Should().Be(1.2);

        // The request hit the canonical delivery-service route, keyed on the
        // caller's id, with the limit forwarded.
        var sent = captured.Single();
        sent.Method.Should().Be(HttpMethod.Get);
        sent.RequestUri!.AbsolutePath.Should().Be("/api/v1/jeebers/jeeber-x/feed");
        sent.RequestUri!.Query.Should().Contain("limit=10");
    }

    [Fact]
    public async Task Feed_Falls_Back_To_TierId_Uuid_When_TierCode_Absent()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse(
            """
            {
              "jeeber_id":"jeeber-y",
              "items":[
                {"request_id":"del_2","tier_id":"uuid-only",
                 "pickup_lat":1,"pickup_lng":2,"distance_km":0.5,
                 "created_at":"2026-06-20T12:00:00Z"}
              ],
              "count":1
            }
            """));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "jeeber-y");
        var resp = await client.GetAsync("/jeebers/me/feed");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JeeberFeedResponse>(JsonOpts);
        body!.Items[0].TierId.Should().Be("uuid-only");
    }

    [Fact]
    public async Task Feed_Upstream_404_Degrades_To_Empty_Feed()
    {
        var stub = new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "jeeber-never-online");
        var resp = await client.GetAsync("/jeebers/me/feed");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<JeeberFeedResponse>(JsonOpts);
        body!.JeeberId.Should().Be("jeeber-never-online");
        body.Count.Should().Be(0);
        body.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Feed_Rejects_NonPositive_Limit()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""{"jeeber_id":"j","items":[],"count":0}"""));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "jeeber-bad-limit");
        var resp = await client.GetAsync("/jeebers/me/feed?limit=0");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Feed_Clamps_Over_Max_Limit()
    {
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse("""{"jeeber_id":"j","items":[],"count":0}""");
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "jeeber-big-limit");
        var resp = await client.GetAsync("/jeebers/me/feed?limit=9999");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The over-max page is clamped to 100 before forwarding upstream.
        captured.Single().RequestUri!.Query.Should().Contain("limit=100");
    }

    [Fact]
    public async Task Feed_Forbidden_For_NonJeeber_Caller()
    {
        var stub = new StubHttpMessageHandler(_ => JsonResponse("""{"jeeber_id":"j","items":[],"count":0}"""));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        // A customer/client caller lacks the jeeber-only AvailabilityToggle
        // capability the feed is gated on.
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "customer-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync("/jeebers/me/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Helpers (mirror MatchingEndpointTests)
    // -----------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> NewFactory(Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        // "driver" is the jeeber role carrying the AvailabilityToggle capability.
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static void ReplaceDeliveryClient(
        IServiceCollection services, HttpMessageHandler handler, string baseUrl)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        services.AddSingleton<IDeliveryServiceClient>(new DeliveryServiceClient(http));
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
