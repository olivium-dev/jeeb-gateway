using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Tiers;

/// <summary>
/// The DISCRIMINATING probe for the D2-b fix: does it engage under the PRODUCTION
/// configuration — <c>FeatureFlags:UseUpstream:Delivery=true</c>, the real DI graph, and GUID
/// tier ids served by a live-shaped delivery-service catalog?
///
/// <para>Every other D2 test drives the resolver directly or runs with the flag OFF, where the
/// gateway-local slug catalog answers and a UUID never appears. That is exactly the
/// configuration the live defect could not be reproduced in. These tests boot the whole app
/// with the flag ON and assert through <c>GET /v1/jeebers/me/feed</c>, so a regression that
/// re-splits the two taxonomies — or that wires the resolver into only some evaluators —
/// fails here.</para>
/// </summary>
public sealed class D2ProductionWiringTests
{
    private const string FeedPath = "/v1/jeebers/me/feed";
    private const string FlashId = "0be308ce-01b5-5cb9-a3e8-9adb60668d9c";
    private const string StandardId = "2bd0d5df-db76-5d14-9e4d-741d60b2fa12";

    private const double JeeberLat = 33.5138;
    private const double JeeberLng = 36.2765;

    [Fact]
    public async Task GuidTier_NearbyRequest_IsListed_UnderProductionFlag()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "nearby-guid", StandardId,
            JeeberLat + 0.01, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        var item = feed.Items.Should().ContainSingle(i => i.RequestId == seeded.Id).Subject;
        item.DistanceMeters.Should().NotBeNull();
        item.DistanceMeters!.Value.Should().BeInRange(1_000, 1_300);
    }

    [Fact]
    public async Task GuidTier_FarRequest_IsStillExcluded_UnderProductionFlag()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);

        // The original D2 pickup point, ~9,000 km away, on the widest (25 km) tier.
        var seeded = await SeedAsync(factory, "client-A", "far-guid", StandardId,
            39.237255, -123.1500317);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id);
        feed.TotalCount.Should().Be(0);
    }

    [Fact]
    public async Task GuidTier_OutsideFlashRadius_IsExcluded_UnderProductionFlag()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);

        // ~5.5 km away on Flash (3 km upstream radius).
        var seeded = await SeedAsync(factory, "client-A", "flash-far", FlashId,
            JeeberLat + 0.05, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id);
    }

    [Fact]
    public async Task UnknownGuidTier_IsExcluded_UnderProductionFlag()
    {
        using var factory = Factory();
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "unknown-guid",
            "11111111-2222-3333-4444-555555555555", JeeberLat + 0.001, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id);
    }

    [Fact]
    public async Task UpstreamTierCatalogFault_ExcludesEverything_UnderProductionFlag()
    {
        using var factory = Factory(faultTiers: true);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);

        var seeded = await SeedAsync(factory, "client-A", "nearby-guid", StandardId,
            JeeberLat + 0.01, JeeberLng);

        var feed = await ReadFeedAsync(jeeber);

        feed.Items.Should().NotContain(i => i.RequestId == seeded.Id,
            "a tier-catalog fault with no cached catalog must fail CLOSED, never allow");
    }

    // ── the short-TTL cache, through the real DI graph ────────────────────────

    [Fact]
    public async Task TheTierCatalog_IsNotReRead_PerRequest()
    {
        using var factory = Factory();
        var upstream = Catalog(factory);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);
        await SeedAsync(factory, "client-A", "nearby-guid", StandardId, JeeberLat + 0.01, JeeberLng);

        // Warm every cached seam on the feed path first, then measure the burst.
        (await ReadFeedAsync(jeeber)).Items.Should().NotBeEmpty();
        var warm = upstream.TierReads;

        for (var i = 0; i < 10; i++)
        {
            (await ReadFeedAsync(jeeber)).Items.Should().NotBeEmpty();
        }

        (upstream.TierReads - warm).Should().Be(0,
            "the D2 catalog read is cached for the TTL — uncached it was one upstream call per "
            + "evaluator per request");
    }

    [Fact]
    public async Task ABriefUpstreamBlip_KeepsServingTheLastGoodCatalog()
    {
        // THE availability point of the cache: a delivery-service blip used to silently empty
        // every live feed, because the local slug catalog cannot resolve an upstream UUID.
        using var factory = Factory();
        var upstream = Catalog(factory);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);
        var seeded = await SeedAsync(factory, "client-A", "nearby-guid", StandardId,
            JeeberLat + 0.01, JeeberLng);

        (await ReadFeedAsync(jeeber)).Items.Should().ContainSingle();

        upstream.Faulting = true;
        ExpireTheCache(factory);

        (await ReadFeedAsync(jeeber)).Items.Should().ContainSingle(i => i.RequestId == seeded.Id,
            "the last good catalog is served through the stale grace window");
    }

    [Fact]
    public async Task AnOutageBeyondTheGraceWindow_FailsClosedAgain()
    {
        using var factory = Factory();
        var upstream = Catalog(factory);
        var jeeber = JeeberClient(factory, out var jeeberId);
        await SetPresenceAsync(factory, jeeberId, JeeberLat, JeeberLng);
        await SeedAsync(factory, "client-A", "nearby-guid", StandardId, JeeberLat + 0.01, JeeberLng);

        (await ReadFeedAsync(jeeber)).Items.Should().ContainSingle();

        upstream.Faulting = true;
        ExhaustTheGraceWindow(factory);

        (await ReadFeedAsync(jeeber)).Items.Should().BeEmpty(
            "past the grace window the catalog is unknown again and D2 fails CLOSED");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static WebApplicationFactory<Program> Factory(bool faultTiers = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(
                    new LiveCatalogDeliveryClient { Faulting = faultTiers });
            });
        });

    private static LiveCatalogDeliveryClient Catalog(WebApplicationFactory<Program> factory)
        => (LiveCatalogDeliveryClient)factory.Services.GetRequiredService<IDeliveryServiceClient>();

    /// <summary>Ages the cached catalog past its TTL but inside the stale grace window.</summary>
    private static void ExpireTheCache(WebApplicationFactory<Program> factory)
        => Advance(factory, TimeSpan.FromSeconds(45));

    private static void ExhaustTheGraceWindow(WebApplicationFactory<Program> factory)
        => Advance(factory, TimeSpan.FromMinutes(30));

    private static void Advance(WebApplicationFactory<Program> factory, TimeSpan by)
        => factory.Services
            .GetRequiredService<JeebGateway.TestControlPlane.FakeTimeProvider>()
            .AdvanceBy(by);

    private sealed class LiveCatalogDeliveryClient : FakeDeliveryPresenceClient
    {
        private int _tierReads;

        public bool Faulting { get; set; }

        public int TierReads => Volatile.Read(ref _tierReads);

        public override Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _tierReads);

            if (Faulting)
            {
                throw new HttpRequestException("delivery-service unreachable");
            }

            IReadOnlyList<DeliveryTierDto> rows = new[]
            {
                Row(FlashId, "Flash", 1, 3.0, 1800),
                Row("efe0629b-0b50-555c-b182-4bd41fcd6507", "Express", 2, 10.0, 7200),
                Row(StandardId, "Standard", 24, 25.0, 86400),
            };
            return Task.FromResult(rows);
        }

        private static DeliveryTierDto Row(string id, string name, int sla, double radiusKm, int ttl) => new()
        {
            Id = id,
            Name = name,
            SlaHours = sla,
            RadiusKm = radiusKm,
            RequestTtlSeconds = ttl,
            CommissionRate = 0.10,
            PriceHint = name,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
        };
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, out string jeeberId)
    {
        jeeberId = $"jeeber-{Guid.NewGuid()}";
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task SetPresenceAsync(
        WebApplicationFactory<Program> factory, string jeeberId, double lat, double lng)
    {
        var delivery = (FakeDeliveryPresenceClient)factory.Services
            .GetRequiredService<IDeliveryServiceClient>();
        await delivery.SetAvailabilityAsync(
            new JeeberAvailabilityUpstreamRequest
            {
                Online = true,
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

    private static async Task<FeedResponse> ReadFeedAsync(HttpClient jeeber)
    {
        var resp = await jeeber.GetAsync(FeedPath);
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<FeedResponse>())!;
    }

    private sealed record FeedResponse(List<FeedItem> Items, int TotalCount);

    private sealed record FeedItem(string RequestId, string? TierId, double? DistanceMeters);
}
