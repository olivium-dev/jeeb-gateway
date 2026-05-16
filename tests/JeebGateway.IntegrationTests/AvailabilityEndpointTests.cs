using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class AvailabilityEndpointTests : IClassFixture<AvailabilityEndpointTests.Fixture>
{
    private readonly Fixture _fixture;

    public AvailabilityEndpointTests(Fixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Patch_Online_Adds_Jeeber_To_Geo_Index_With_Vehicle_And_Zone()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "motorbike",
            zone = "amman-downtown",
            longitude = 35.9106,
            latitude = 31.9539
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityBody>();
        body!.Online.Should().BeTrue();
        body.VehicleType.Should().Be("motorbike");
        body.Zone.Should().Be("amman-downtown");

        var geo = factory.Services.GetRequiredService<IGeoIndex>();
        (await geo.ContainsAsync(userId, default)).Should().BeTrue();
        (await geo.GetVehicleAsync(userId, default)).Should().Be(VehicleType.Motorbike);
    }

    [Fact]
    public async Task Patch_Offline_Removes_From_Geo_Index_And_Withdraws_Pending_Offers()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var goOnline = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "car",
            zone = "amman-shmeisani"
        });
        goOnline.EnsureSuccessStatusCode();

        var offers = factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        offers.EnqueueForTest(userId, "offer-A");
        offers.EnqueueForTest(userId, "offer-B");

        var goOffline = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });
        goOffline.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await goOffline.Content.ReadFromJsonAsync<AvailabilityBody>();
        body!.Online.Should().BeFalse();
        body.WithdrawnOffers.Should().Be(2);

        var geo = factory.Services.GetRequiredService<IGeoIndex>();
        (await geo.ContainsAsync(userId, default)).Should().BeFalse();
        offers.PeekForTest(userId).Should().BeEmpty();
    }

    [Fact]
    public async Task Patch_Online_Without_Vehicle_Returns_400()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            zone = "amman-downtown"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Online_Without_Zone_Returns_400()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "car"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Online_With_Bad_Vehicle_Returns_400()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "submarine",
            zone = "amman-downtown"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Without_Identity_Returns_401()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_Body_Missing_Online_Returns_400()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { vehicleType = "car" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Sweeper_Auto_Offlines_After_30min_Inactivity_And_Sends_Push()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var clock = (FakeClock)factory.Services.GetRequiredService<TimeProvider>();

        var goOnline = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "scooter",
            zone = "amman-abdoun"
        });
        goOnline.EnsureSuccessStatusCode();

        var sweeper = factory.Services
            .GetServices<IHostedService>()
            .OfType<AutoOfflineSweeper>()
            .Single();

        // Within window — still online.
        clock.Advance(TimeSpan.FromMinutes(29));
        await sweeper.SweepOnceAsync(default);

        var geo = factory.Services.GetRequiredService<IGeoIndex>();
        (await geo.ContainsAsync(userId, default)).Should().BeTrue();

        // Past 30 min — flipped offline.
        clock.Advance(TimeSpan.FromMinutes(2));
        await sweeper.SweepOnceAsync(default);

        (await geo.ContainsAsync(userId, default)).Should().BeFalse();

        var notifier = (InMemoryAutoOfflineNotifier)factory.Services.GetRequiredService<IAutoOfflineNotifier>();
        notifier.Sent.Should().ContainSingle(s => s.UserId == userId);
    }

    [Fact]
    public async Task Auto_Offline_Push_Flows_Through_PushNotificationService()
    {
        var factory = _fixture.FactoryWithProductionNotifier();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var devices = factory.Services.GetRequiredService<IDeviceTokenStore>();
        await devices.RegisterAsync(new DeviceToken(userId, DevicePlatform.Fcm, $"tok-{userId}"), default);

        await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "car",
            zone = "amman-downtown"
        });

        var clock = (FakeClock)factory.Services.GetRequiredService<TimeProvider>();
        clock.Advance(TimeSpan.FromMinutes(31));

        var sweeper = factory.Services.GetServices<IHostedService>().OfType<AutoOfflineSweeper>().Single();
        await sweeper.SweepOnceAsync(default);

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Should().ContainSingle(s =>
            s.Request.UserId == userId &&
            s.Request.Trigger == NotificationTrigger.AutoOffline);
    }

    [Theory]
    [InlineData("car", VehicleType.Car)]
    [InlineData("motorbike", VehicleType.Motorbike)]
    [InlineData("bicycle", VehicleType.Bicycle)]
    [InlineData("scooter", VehicleType.Scooter)]
    [InlineData("walk", VehicleType.Walk)]
    public async Task Patch_Online_Broadcasts_Each_Vehicle_Type(string wire, VehicleType expected)
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = wire,
            zone = "amman-downtown"
        });
        resp.EnsureSuccessStatusCode();

        var geo = factory.Services.GetRequiredService<IGeoIndex>();
        (await geo.GetVehicleAsync(userId, default)).Should().Be(expected);
    }

    [Fact]
    public async Task Sweeper_Skips_Jeebers_With_Recent_Interaction()
    {
        var factory = _fixture.Factory();
        var client = factory.CreateClient();
        var userId = $"jeeber-{Guid.NewGuid()}";
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var clock = (FakeClock)factory.Services.GetRequiredService<TimeProvider>();

        await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "bicycle",
            zone = "amman-jabal-amman"
        });

        clock.Advance(TimeSpan.FromMinutes(25));

        // Recent GET counts as an interaction.
        var get = await client.GetAsync("/jeebers/me/availability");
        get.EnsureSuccessStatusCode();

        clock.Advance(TimeSpan.FromMinutes(20));

        var sweeper = factory.Services
            .GetServices<IHostedService>()
            .OfType<AutoOfflineSweeper>()
            .Single();
        await sweeper.SweepOnceAsync(default);

        var geo = factory.Services.GetRequiredService<IGeoIndex>();
        (await geo.ContainsAsync(userId, default)).Should().BeTrue();
    }

    public sealed class Fixture
    {
        public WebApplicationFactory<Program> Factory() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var clock = services.Single(d => d.ServiceType == typeof(TimeProvider));
                    services.Remove(clock);
                    services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)));

                    // Swap the production push-backed notifier for the in-memory
                    // variant so we can assert what was sent without FCM.
                    var notifier = services.Single(d => d.ServiceType == typeof(IAutoOfflineNotifier));
                    services.Remove(notifier);
                    services.AddSingleton<InMemoryAutoOfflineNotifier>();
                    services.AddSingleton<IAutoOfflineNotifier>(sp => sp.GetRequiredService<InMemoryAutoOfflineNotifier>());
                });
            });

        /// <summary>
        /// Variant that keeps the production <see cref="PushAutoOfflineNotifier"/>
        /// wired so we can assert the auto-offline trigger really flows through
        /// the shared push pipeline (preferences, transports, retry).
        /// </summary>
        public WebApplicationFactory<Program> FactoryWithProductionNotifier() => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    var clock = services.Single(d => d.ServiceType == typeof(TimeProvider));
                    services.Remove(clock);
                    services.AddSingleton<TimeProvider>(new FakeClock(new DateTimeOffset(2026, 5, 15, 12, 0, 0, TimeSpan.Zero)));
                });
            });
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now;

        public FakeClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed record AvailabilityBody(
        string UserId,
        bool Online,
        string VehicleType,
        string? Zone,
        double? Longitude,
        double? Latitude,
        DateTimeOffset? LastSeenAt,
        DateTimeOffset? LastInteractionAt,
        DateTimeOffset UpdatedAt,
        int WithdrawnOffers);
}
