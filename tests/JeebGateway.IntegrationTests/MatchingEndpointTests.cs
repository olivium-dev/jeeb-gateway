using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Matching;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-008 acceptance criteria:
///
///   AC1. Radius query returns Jeebers within tier-specific km.
///   AC2. Results filtered by vehicle type.
///   AC3. Results ordered by proximity then rating.
///   AC4. Push sent to matched Jeebers within 2 seconds.
///   AC5. Client sees count of notified Jeebers.
///   AC6. Query completes in &lt; 500ms for 10k online Jeebers.
///
/// Each test takes a fresh factory (and therefore a fresh availability
/// store, ratings provider, push transport) so cases don't share state.
/// </summary>
public class MatchingEndpointTests
{
    // Riyadh, Diplomatic Quarter — used as the canonical pickup point so
    // distance assertions read in real Earth units.
    private const double PickupLat = 24.6309;
    private const double PickupLng = 46.7194;

    [Fact]
    public async Task Matching_Returns_Jeebers_Within_Tier_Radius()
    {
        // AC1: a Jeeber 3 km away is inside the 5 km "urgent" ring; one 20 km
        // away is outside it. Only the inside-ring Jeeber should be returned.
        using var factory = NewFactory();
        await GoOnline(factory, "j-near", VehicleType.Car, PickupLat + KmToLatDelta(3), PickupLng);
        await GoOnline(factory, "j-far", VehicleType.Car, PickupLat + KmToLatDelta(20), PickupLng);

        var client = ClientFor(factory, "client-1");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.RadiusKm.Should().Be(5.0);
        body.Candidates.Should().ContainSingle()
            .Which.UserId.Should().Be("j-near");
    }

    [Fact]
    public async Task Matching_Filters_By_Vehicle_Type()
    {
        // AC2: when the caller restricts to motorbikes, a co-located Jeeber
        // on a car is excluded even though they're well inside the radius.
        using var factory = NewFactory();
        await GoOnline(factory, "j-car", VehicleType.Car, PickupLat + KmToLatDelta(1), PickupLng);
        await GoOnline(factory, "j-moto", VehicleType.Motorbike, PickupLat + KmToLatDelta(2), PickupLng);

        var client = ClientFor(factory, "client-veh");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent",
            allowedVehicleTypes = new[] { "motorbike" }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.Candidates.Should().ContainSingle()
            .Which.UserId.Should().Be("j-moto");
    }

    [Fact]
    public async Task Matching_Orders_By_Proximity_Then_Rating()
    {
        // AC3: two Jeebers at the same distance break ties on rating
        // (higher first); a closer Jeeber outranks a higher-rated one
        // farther away.
        using var factory = NewFactory();
        var ratings = factory.Services.GetRequiredService<InMemoryJeeberRatingProvider>();
        ratings.SetRating("j-close-low", 3.0);
        ratings.SetRating("j-mid-high", 4.9);
        ratings.SetRating("j-tied-a", 4.0);
        ratings.SetRating("j-tied-b", 4.8);

        await GoOnline(factory, "j-close-low", VehicleType.Car, PickupLat + KmToLatDelta(0.5), PickupLng);
        await GoOnline(factory, "j-mid-high", VehicleType.Car, PickupLat + KmToLatDelta(2.0), PickupLng);
        await GoOnline(factory, "j-tied-a", VehicleType.Car, PickupLat + KmToLatDelta(1.0), PickupLng);
        await GoOnline(factory, "j-tied-b", VehicleType.Car, PickupLat + KmToLatDelta(1.0), PickupLng);

        var client = ClientFor(factory, "client-order");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        var ids = body!.Candidates.Select(c => c.UserId).ToArray();
        // j-close-low (0.5km, 3.0)  → 1st (proximity wins despite low rating)
        // j-tied-b (1.0km, 4.8)     → 2nd (tied distance, higher rating)
        // j-tied-a (1.0km, 4.0)     → 3rd (tied distance, lower rating)
        // j-mid-high (2.0km, 4.9)   → 4th (farthest)
        ids.Should().Equal("j-close-low", "j-tied-b", "j-tied-a", "j-mid-high");
    }

    [Fact]
    public async Task Matching_Reports_Notified_Count_And_Sends_Push()
    {
        // AC5: the response carries the notified count, and the in-memory
        // push transport sees one new-offer push per matched Jeeber.
        using var factory = NewFactory();
        await RegisterDevice(factory, "j-push-a");
        await RegisterDevice(factory, "j-push-b");
        await GoOnline(factory, "j-push-a", VehicleType.Car, PickupLat + KmToLatDelta(1), PickupLng);
        await GoOnline(factory, "j-push-b", VehicleType.Car, PickupLat + KmToLatDelta(2), PickupLng);

        var client = ClientFor(factory, "client-count");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.NotifiedCount.Should().Be(2);
        body.CandidateCount.Should().Be(2);

        var fcm = factory.Services.GetServices<IPushTransport>()
            .OfType<InMemoryPushTransport>()
            .Single(t => t.Platform == DevicePlatform.Fcm);
        fcm.Sent.Should().HaveCount(2);
        fcm.Sent.Should().OnlyContain(s => s.Request.Trigger == NotificationTrigger.NewOffer);
    }

    [Fact]
    public async Task Matching_Push_Sent_Within_2_Seconds()
    {
        // AC4: the entire fan-out must complete under the 2-second SLA.
        // The in-memory transport is instantaneous; this guards against
        // a long timeout being introduced silently.
        using var factory = NewFactory();
        for (var i = 0; i < 10; i++)
        {
            await RegisterDevice(factory, $"j-sla-{i}");
            await GoOnline(factory, $"j-sla-{i}", VehicleType.Car,
                PickupLat + KmToLatDelta(0.1 * (i + 1)), PickupLng);
        }

        var client = ClientFor(factory, "client-sla");
        var sw = Stopwatch.StartNew();
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });
        sw.Stop();

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.NotifiedCount.Should().Be(10);
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2),
            "AC4: push sent to matched Jeebers within 2 seconds");
    }

    [Fact]
    public async Task Matching_Completes_Under_500ms_For_10k_Online_Jeebers()
    {
        // AC6: the radius scan + sort must complete under 500ms with 10k
        // online Jeebers. We seed the in-memory store directly (the public
        // /jeebers/me/availability endpoint would force one HTTP round-trip
        // per Jeeber, swamping the test runner). MaxNotified is lowered to
        // 1 so the fan-out doesn't dominate the wall clock — the AC scopes
        // strictly to the query path, not the push surface.
        using var factory = NewFactory(opts =>
        {
            opts.MaxNotified = 1;
        });

        var availability = factory.Services.GetRequiredService<IAvailabilityStore>();
        var rng = new Random(42);
        for (var i = 0; i < 10_000; i++)
        {
            // Spread Jeebers in a ~50 km square centred on the pickup so
            // some land inside every tier ring and most land outside the
            // 5 km "urgent" ring — exercises both branches of the filter.
            var dLat = (rng.NextDouble() - 0.5) * KmToLatDelta(100); // ±50 km lat span
            var dLng = (rng.NextDouble() - 0.5) * KmToLatDelta(100);
            await availability.GoOnlineAsync($"j-perf-{i}", new GoOnlineRequest
            {
                VehicleType = VehicleType.Car,
                Zone = "perf",
                Latitude = PickupLat + dLat,
                Longitude = PickupLng + dLng
            }, default);
        }

        var matching = factory.Services.GetRequiredService<IMatchingService>();
        // Warm the JIT so the first run's CLR warm-up isn't counted.
        await matching.RunAsync(new MatchingInput
        {
            RequestId = "warm",
            PickupLat = PickupLat,
            PickupLng = PickupLng,
            TierId = "urgent",
            AllowedVehicleTypes = new HashSet<VehicleType>()
        }, default);

        var sw = Stopwatch.StartNew();
        var outcome = await matching.RunAsync(new MatchingInput
        {
            RequestId = "perf",
            PickupLat = PickupLat,
            PickupLng = PickupLng,
            TierId = "urgent",
            AllowedVehicleTypes = new HashSet<VehicleType>()
        }, default);
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(500),
            $"AC6: matching over 10k Jeebers ran in {sw.ElapsedMilliseconds}ms");
        outcome.Candidates.Should().NotBeEmpty("uniform distribution must put at least one Jeeber inside 5km");
    }

    [Fact]
    public async Task Matching_Excludes_Offline_Jeebers()
    {
        // Online → offline must immediately remove a Jeeber from candidates.
        using var factory = NewFactory();
        await GoOnline(factory, "j-toggle", VehicleType.Car, PickupLat + KmToLatDelta(1), PickupLng);
        var availability = factory.Services.GetRequiredService<IAvailabilityStore>();
        await availability.GoOfflineAsync("j-toggle", GoOfflineReason.UserToggle, default);

        var client = ClientFor(factory, "client-off");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.Candidates.Should().BeEmpty();
        body.NotifiedCount.Should().Be(0);
    }

    [Fact]
    public async Task Matching_Excludes_Jeebers_Without_Gps()
    {
        // A Jeeber who went online without sharing GPS shouldn't be matched
        // by a radius query — there's no point to compute against.
        using var factory = NewFactory();
        var availability = factory.Services.GetRequiredService<IAvailabilityStore>();
        await availability.GoOnlineAsync("j-nogps", new GoOnlineRequest
        {
            VehicleType = VehicleType.Car,
            Zone = "amman-downtown"
            // intentionally no Latitude / Longitude
        }, default);

        var client = ClientFor(factory, "client-nogps");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.Candidates.Should().BeEmpty();
    }

    [Fact]
    public async Task Matching_Returns_400_For_Unknown_Tier()
    {
        using var factory = NewFactory();
        var client = ClientFor(factory, "client-bad-tier");

        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "no-such-tier"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Matching_Returns_400_For_Unknown_Vehicle_Type()
    {
        using var factory = NewFactory();
        var client = ClientFor(factory, "client-bad-veh");

        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent",
            allowedVehicleTypes = new[] { "submarine" }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Matching_Returns_400_For_Invalid_Coordinates()
    {
        using var factory = NewFactory();
        var client = ClientFor(factory, "client-bad-loc");

        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = 99.0,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Matching_Requires_Identity()
    {
        using var factory = NewFactory();
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Matching_Rejects_Caller_Without_Client_Role()
    {
        using var factory = NewFactory();
        var jeeberOnly = factory.CreateClient();
        jeeberOnly.DefaultRequestHeaders.Add("X-User-Id", "jeeber-only");
        jeeberOnly.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await jeeberOnly.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Matching_Tier_Specific_Radius_Wider_Tier_Returns_More()
    {
        // AC1 (companion case): a Jeeber outside "urgent" (5km) but inside
        // "same-day" (15km) must appear only for the wider tier.
        using var factory = NewFactory();
        await GoOnline(factory, "j-7km", VehicleType.Car, PickupLat + KmToLatDelta(7), PickupLng);

        var client = ClientFor(factory, "client-tier");
        var narrow = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "urgent"
        });
        narrow.StatusCode.Should().Be(HttpStatusCode.OK);
        (await narrow.Content.ReadFromJsonAsync<MatchingRunResponse>())!.Candidates
            .Should().BeEmpty("7km is outside the 5km urgent ring");

        var wide = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "same-day"
        });
        wide.StatusCode.Should().Be(HttpStatusCode.OK);
        (await wide.Content.ReadFromJsonAsync<MatchingRunResponse>())!.Candidates
            .Should().ContainSingle()
            .Which.UserId.Should().Be("j-7km");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>
    /// At Riyadh latitude, 1° of latitude ≈ 111 km. Converting km to a
    /// latitude delta keeps test fixtures readable — Haversine still
    /// produces realistic distances because we move strictly along the
    /// meridian (no cosine compression on the lat axis).
    /// </summary>
    private static double KmToLatDelta(double km) => km / 111.0;

    private static WebApplicationFactory<Program> NewFactory(Action<MatchingOptions>? configure = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                if (configure is not null)
                {
                    services.Configure(configure);
                }
            });
        });
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static async Task GoOnline(
        WebApplicationFactory<Program> factory,
        string jeeberId,
        VehicleType vehicle,
        double lat,
        double lng)
    {
        var store = factory.Services.GetRequiredService<IAvailabilityStore>();
        await store.GoOnlineAsync(jeeberId, new GoOnlineRequest
        {
            VehicleType = vehicle,
            Zone = "test-zone",
            Latitude = lat,
            Longitude = lng
        }, default);
    }

    private static async Task RegisterDevice(WebApplicationFactory<Program> factory, string userId)
    {
        var devices = factory.Services.GetRequiredService<IDeviceTokenStore>();
        await devices.RegisterAsync(new DeviceToken(userId, DevicePlatform.Fcm, $"tok-{userId}"), default);
    }
}
