using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class AdminZonesEndpointTests : IClassFixture<AdminZonesEndpointTests.Fixture>
{
    private readonly Fixture _fixture;

    public AdminZonesEndpointTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Get_Without_Identity_Returns_401()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Get_Without_Admin_Role_Returns_403()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-1");

        var resp = await client.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Get_Groups_Online_Jeebers_By_Configured_Boundary_With_Vehicle_Type()
    {
        var factory = _fixture.Factory();
        await GoOnline(factory, "jeeber-downtown-a", "motorbike", "beirut-downtown",  lon: 35.500, lat: 33.898);
        await GoOnline(factory, "jeeber-downtown-b", "car",       "beirut-downtown",  lon: 35.503, lat: 33.900);
        await GoOnline(factory, "jeeber-achrafieh-a", "bicycle",  "beirut-achrafieh", lon: 35.525, lat: 33.888);
        // Offline Jeeber must not surface on the ops map even though it has a location.
        await GoOnline(factory, "jeeber-going-offline", "scooter", "beirut-hamra", lon: 35.484, lat: 33.896);
        await GoOffline(factory, "jeeber-going-offline");

        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminZoneViewResponse>();
        body.Should().NotBeNull();
        body!.TotalOnline.Should().Be(3);
        body.RefreshIntervalSeconds.Should().Be(30);

        var downtown = body.Zones.Single(z => z.Key == "beirut-downtown");
        downtown.Count.Should().Be(2);
        downtown.CountByVehicleType.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["motorbike"] = 1,
            ["car"] = 1
        });
        downtown.Jeebers.Select(j => j.UserId).Should().BeEquivalentTo(new[]
        {
            "jeeber-downtown-a",
            "jeeber-downtown-b"
        });
        downtown.Bounds.Should().NotBeNull();
        downtown.Bounds!.MinLatitude.Should().Be(33.893);
        downtown.Jeebers.Should().OnlyContain(j => j.Latitude.HasValue && j.Longitude.HasValue);

        var achrafieh = body.Zones.Single(z => z.Key == "beirut-achrafieh");
        achrafieh.Count.Should().Be(1);
        achrafieh.Jeebers.Single().VehicleType.Should().Be("bicycle");

        var hamra = body.Zones.Single(z => z.Key == "beirut-hamra");
        hamra.Count.Should().Be(0);
        hamra.Jeebers.Should().BeEmpty();
    }

    [Fact]
    public async Task Get_Buckets_Jeebers_Outside_Configured_Zones_As_Unzoned()
    {
        var factory = _fixture.Factory();
        await GoOnline(factory, "jeeber-zarqa", "car", "zarqa", lon: 36.1, lat: 32.07);
        await GoOnlineWithoutLocation(factory, "jeeber-no-loc", "walk", "unknown");

        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/zones/online-jeebers");

        var body = await resp.Content.ReadFromJsonAsync<AdminZoneViewResponse>();
        var unzoned = body!.Zones.Single(z => z.Key == ZoneOptions.UnzonedKey);
        unzoned.Count.Should().Be(2);
        unzoned.Bounds.Should().BeNull();
        unzoned.Jeebers.Select(j => j.UserId).Should().BeEquivalentTo(new[]
        {
            "jeeber-zarqa",
            "jeeber-no-loc"
        });
    }

    [Fact]
    public async Task Get_Sets_Cache_Control_Header_To_Configured_Refresh_Interval()
    {
        var factory = _fixture.Factory();
        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Headers.CacheControl.Should().NotBeNull();
        resp.Headers.CacheControl!.MaxAge.Should().Be(TimeSpan.FromSeconds(30));
        resp.Headers.CacheControl.Public.Should().BeTrue();
    }

    [Fact]
    public async Task Get_Honours_Configurable_Boundaries_Override()
    {
        var factory = _fixture.FactoryWithBoundaries(new[]
        {
            new ZoneBoundary
            {
                Key = "irbid-centre",
                Name = "Irbid",
                MinLatitude = 32.50,
                MaxLatitude = 32.60,
                MinLongitude = 35.80,
                MaxLongitude = 35.90
            }
        });

        await GoOnline(factory, "jeeber-irbid", "car", "irbid-centre", lon: 35.85, lat: 32.55);
        await GoOnline(factory, "jeeber-amman", "car", "amman-downtown", lon: 35.94, lat: 31.95);

        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/zones/online-jeebers");

        var body = await resp.Content.ReadFromJsonAsync<AdminZoneViewResponse>();
        body!.Zones.Should().HaveCount(2);
        body.Zones.Select(z => z.Key).Should().BeEquivalentTo(new[]
        {
            "irbid-centre",
            ZoneOptions.UnzonedKey
        });
        body.Zones.Single(z => z.Key == "irbid-centre").Jeebers.Single().UserId.Should().Be("jeeber-irbid");
        body.Zones.Single(z => z.Key == ZoneOptions.UnzonedKey).Jeebers.Single().UserId.Should().Be("jeeber-amman");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-admin");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static async Task GoOnline(
        WebApplicationFactory<Program> factory,
        string userId,
        string vehicleType,
        string zone,
        double lon,
        double lat)
    {
        var client = JeeberClient(factory, userId);
        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType,
            zone,
            longitude = lon,
            latitude = lat
        });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task GoOnlineWithoutLocation(
        WebApplicationFactory<Program> factory,
        string userId,
        string vehicleType,
        string zone)
    {
        var client = JeeberClient(factory, userId);
        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType,
            zone
        });
        resp.EnsureSuccessStatusCode();
    }

    private static async Task GoOffline(WebApplicationFactory<Program> factory, string userId)
    {
        var client = JeeberClient(factory, userId);
        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });
        resp.EnsureSuccessStatusCode();
    }

    public sealed class Fixture
    {
        public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                // S06: GoOnline/GoOffline now PATCH availability THROUGH
                // delivery-service. The admin ops-map still reads the mirrored
                // in-memory store; swap the upstream client for the in-process
                // presence fake so the toggle resolves without a live Go upstream.
                builder.ConfigureServices(UseFakeDeliveryPresence);
            });

        public WebApplicationFactory<Program> FactoryWithBoundaries(IEnumerable<ZoneBoundary> boundaries)
        {
            var snapshot = boundaries.ToList();
            return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                // PostConfigure replaces whatever the appsettings binder
                // produced. This is the simplest way to swap the default
                // configured boundaries for a deterministic test set without
                // wrestling with array-merge semantics in IConfiguration.
                builder.ConfigureServices(services =>
                {
                    services.PostConfigure<ZoneOptions>(opts =>
                    {
                        opts.Boundaries = snapshot;
                    });

                    UseFakeDeliveryPresence(services);
                });
            });
        }

        private static void UseFakeDeliveryPresence(IServiceCollection services)
        {
            services.RemoveAll<IDeliveryServiceClient>();
            services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
        }
    }

    private sealed class AdminZoneViewResponse
    {
        public List<AdminZoneGroup> Zones { get; set; } = new();
        public int TotalOnline { get; set; }
        public DateTimeOffset GeneratedAt { get; set; }
        public int RefreshIntervalSeconds { get; set; }
    }

    private sealed class AdminZoneGroup
    {
        public string Key { get; set; } = "";
        public string? Name { get; set; }
        public ZoneBounds? Bounds { get; set; }
        public int Count { get; set; }
        public Dictionary<string, int> CountByVehicleType { get; set; } = new();
        public List<AdminJeeberMarker> Jeebers { get; set; } = new();
    }

    private sealed class ZoneBounds
    {
        public double MinLatitude { get; set; }
        public double MaxLatitude { get; set; }
        public double MinLongitude { get; set; }
        public double MaxLongitude { get; set; }
    }

    private sealed class AdminJeeberMarker
    {
        public string UserId { get; set; } = "";
        public string VehicleType { get; set; } = "";
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
    }
}
