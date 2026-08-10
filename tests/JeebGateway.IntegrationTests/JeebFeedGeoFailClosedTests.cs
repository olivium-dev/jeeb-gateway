using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Bug D2 end-to-end on the feed surface: an out-of-radius or unknown-distance request must be
/// absent from <c>GET /v1/jeebers/me/feed</c>, and a listed item must carry a real
/// <c>distanceMeters</c>.
/// </summary>
public sealed class JeebFeedGeoFailClosedTests
{
    private const string FeedPath = "/v1/jeebers/me/feed";

    // The seeded catalog radii the fix keys on: urgent 3 km, same-day 10 km, scheduled 25 km.
    private const double JeeberLat = 33.5138;
    private const double JeeberLng = 36.2765;

    [Fact]
    public async Task A_request_beyond_the_tier_radius_is_not_listed()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        // The exact D2 pickup point, ~9,000 km from the jeeber, on the 25 km tier.
        var seeded = await SeedAsync(factory, "client-A", "far away", "scheduled",
            39.237255, -123.1500317);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id,
            "a 25 km tier must not surface a ~9,000 km request — this is bug D2");
        feed.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task A_request_inside_the_tier_radius_is_listed_with_a_real_distance()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        // ~1.1 km away, well inside the 3 km urgent radius.
        var seeded = await SeedAsync(factory, "client-A", "nearby", "urgent",
            JeeberLat + 0.01, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        var item = feed.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.DistanceMeters.Should().NotBeNull(
            "distanceMeters:null was the D2 tell that no distance was ever computed");
        item.DistanceMeters!.Value.Should().BeInRange(1_000, 1_300);
    }

    [Fact]
    public async Task A_jeeber_with_no_location_fix_gets_an_empty_feed()
    {
        // The accepted availability cost of failing closed: no fix means nothing can be proven
        // in range, so nothing is shown. It must NOT fall through to the unfiltered list.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, lat: null, lng: null);

        await SeedAsync(factory, "client-A", "nearby", "urgent", JeeberLat + 0.01, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().BeEmpty();
        feed.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task A_request_with_no_pickup_coordinates_is_not_listed()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "no coords", "urgent", null, null);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id);
    }

    [Fact]
    public async Task A_request_on_an_unknown_tier_is_not_listed()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "opaque tier",
            Guid.NewGuid().ToString(), JeeberLat + 0.01, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id,
            "an unresolvable tier has no radius to test against, so it cannot be proven in range");
    }

    [Fact]
    public async Task A_request_with_no_tier_at_all_is_not_listed()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "no tier", null,
            JeeberLat + 0.01, JeeberLng);

        (await ReadFeedAsync(jeeber)).Items.Should().NotContain(i => i.RequestId == seeded.Id);
    }

    [Fact]
    public async Task A_legacy_tier_code_still_resolves_to_its_catalog_radius()
    {
        // `standard` aliases to same-day (10 km); a 1.1 km pickup must stay visible so the
        // fail-closed cut does not silently blank every legacy-coded client.
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "legacy code", "standard",
            JeeberLat + 0.01, JeeberLng);

        (await ReadFeedAsync(jeeber)).Items.Should().Contain(i => i.RequestId == seeded.Id);
    }

    [Fact]
    public async Task Only_the_in_range_request_survives_when_both_exist()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, online: true, JeeberLat, JeeberLng);

        var near = await SeedAsync(factory, "client-A", "near", "urgent",
            JeeberLat + 0.01, JeeberLng);
        var far = await SeedAsync(factory, "client-A", "far", "urgent",
            39.237255, -123.1500317);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().ContainSingle().Which.RequestId.Should().Be(near.Id);
        feed.Items.Should().NotContain(i => i.RequestId == far.Id);
    }

    // ── harness (mirrors JeebFeedTests) ──────────────────────────────────────────────

    private static async Task<FeedResponse> ReadFeedAsync(HttpClient jeeber)
    {
        var resp = await jeeber.GetAsync(FeedPath);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<FeedResponse>())!;
    }

    private static WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            }));

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, out string jeeberId)
    {
        jeeberId = $"jeeber-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task SetPresenceAsync(
        WebApplicationFactory<Program> factory, string jeeberId, bool online,
        double? lat, double? lng)
    {
        var delivery = (FakeDeliveryPresenceClient)factory.Services
            .GetRequiredService<IDeliveryServiceClient>();
        await delivery.SetAvailabilityAsync(
            new JeeberAvailabilityUpstreamRequest
            {
                Online = online,
                VehicleType = "car",
                Zone = "downtown",
                Lat = lat,
                Lng = lng,
            },
            jeeberId,
            default);
    }

    private static Task<DeliveryRequest> SeedAsync(
        WebApplicationFactory<Program> factory,
        string clientId, string description, string? tierId,
        double? pickupLat, double? pickupLng)
        => factory.Services.GetRequiredService<IRequestsStore>().CreateAsync(
            new CreateRequestInput
            {
                ClientId = clientId,
                Description = description,
                TierId = tierId,
                PickupAddress = "Pickup",
                PickupLocation = pickupLat is { } la && pickupLng is { } ln
                    ? new GeoPoint { Lat = la, Lng = ln }
                    : null,
            },
            default);

    private sealed record FeedResponse(List<FeedItem> Items, int TotalCount);

    private sealed record FeedItem(string RequestId, string? TierId, double? DistanceMeters);
}
