using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-014 / JEEB-32 integration tests for the GPS streaming and
/// SSE tracking endpoints. Covers:
///
///   AC1. POST /location/update accepts a batch of points and records
///        the most-recent (by device timestamp) as the Jeeber's latest fix.
///   AC2. GET /deliveries/{id}/tracking emits SSE frames carrying the
///        latest position and a straight-line polyline to the dropoff.
///   AC3. When the latest fix ages beyond the stale threshold, the SSE
///        stream switches the event name to <c>last-seen</c>.
///   AC4. Validation: malformed payloads (out-of-range lat/lng, empty
///        batch, oversized batch) are rejected with 400.
///   AC5. Authorisation: unauthenticated callers get 401; non-participants
///        get 403; missing deliveries get 404.
/// </summary>
public class LocationTrackingTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public LocationTrackingTests(WebApplicationFactory<Program> factory)
    {
        // S06 presence wire: POST /location/update now forwards the latest fix to
        // delivery-service as a heartbeat. Swap the real HTTP client for the
        // in-process presence fake so the GPS ingest path resolves without a live
        // Go upstream; the in-memory ILocationStore (asserted by these tests) is
        // untouched.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            });
        });
    }

    // ---- POST /location/update --------------------------------------------------

    [Fact]
    public async Task Update_Accepts_Batch_And_Records_Latest()
    {
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var http = AuthClient(jeeberId);

        var now = DateTimeOffset.UtcNow;
        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.7100, lng = 46.6700, accuracy = 12.5, timestamp = now.AddSeconds(-10) },
                new { lat = 24.7110, lng = 46.6710, accuracy = 8.0,  timestamp = now.AddSeconds(-5) },
                new { lat = 24.7120, lng = 46.6720, accuracy = 6.5,  timestamp = now },
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(3);
        body.Rejected.Should().Be(0);
        body.Latest.Should().NotBeNull();
        body.Latest!.Lat.Should().Be(24.7120);
        body.Latest.Lng.Should().Be(46.6720);

        // The store retained the most-recent (by device timestamp) point.
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        var latest = await store.GetLatestAsync(jeeberId);
        latest.Should().NotBeNull();
        latest!.Lat.Should().Be(24.7120);
    }

    [Fact]
    public async Task Update_Out_Of_Order_Batch_Picks_Newest_By_Device_Timestamp()
    {
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var http = AuthClient(jeeberId);
        var now = DateTimeOffset.UtcNow;

        // Newer point delivered first, older next — the store must still
        // retain the device-newest one as the "latest" fix.
        await http.PostAsJsonAsync("/location/update", new
        {
            points = new[] { new { lat = 24.0, lng = 46.0, accuracy = (double?)null, timestamp = now } }
        });
        await http.PostAsJsonAsync("/location/update", new
        {
            points = new[] { new { lat = 25.0, lng = 47.0, accuracy = (double?)null, timestamp = now.AddSeconds(-60) } }
        });

        var store = _factory.Services.GetRequiredService<ILocationStore>();
        var latest = await store.GetLatestAsync(jeeberId);
        latest!.Lat.Should().Be(24.0, "device-newer point wins over an out-of-order older delivery");
    }

    [Fact]
    public async Task Update_Empty_Batch_Returns_400()
    {
        var http = AuthClient($"jeeber-{Guid.NewGuid()}");
        var resp = await http.PostAsJsonAsync("/location/update", new { points = Array.Empty<object>() });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_Out_Of_Range_Coordinates_Are_Counted_As_Rejected()
    {
        var http = AuthClient($"jeeber-{Guid.NewGuid()}");
        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new[]
            {
                new { lat = 200.0, lng = 46.0, accuracy = (double?)null, timestamp = DateTimeOffset.UtcNow }
            }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(0);
        body.Rejected.Should().Be(1);
        body.Latest.Should().BeNull();
    }

    [Fact]
    public async Task Update_Without_Identity_Returns_401()
    {
        var http = _factory.CreateClient();
        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new[] { new { lat = 1.0, lng = 1.0, timestamp = DateTimeOffset.UtcNow } }
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Update_Oversized_Batch_Returns_400()
    {
        var http = AuthClient($"jeeber-{Guid.NewGuid()}");
        var now = DateTimeOffset.UtcNow;
        var points = Enumerable.Range(0, 300)
            .Select(i => new { lat = 1.0, lng = 1.0, accuracy = (double?)null, timestamp = now.AddSeconds(-i) })
            .ToArray();

        var resp = await http.PostAsJsonAsync("/location/update", new { points });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---- 50k updates / minute throughput smoke ---------------------------------

    [Fact]
    public void Store_Sustains_50k_Updates_Per_Minute_With_Parallel_Writers()
    {
        // The AC requires 50k updates/minute (~833/sec). We run a short
        // 200 ms burst with parallel writers and assert the achieved rate
        // is comfortably above the budget. This guards against the
        // ConcurrentDictionary write path regressing into a global lock.
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        var now = DateTimeOffset.UtcNow;
        const int writers = 8;
        const int perWriter = 2_000;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        Parallel.For(0, writers, w =>
        {
            var id = $"throughput-jeeber-{w}";
            for (var i = 0; i < perWriter; i++)
            {
                // In-memory store (flag-OFF): RecordAsync completes synchronously
                // (Task.FromResult), so this throughput smoke stays valid.
                store.RecordAsync(id, new[]
                {
                    new GpsPointDto
                    {
                        Lat = 24.0 + (i * 0.0001),
                        Lng = 46.0 + (i * 0.0001),
                        Accuracy = 5,
                        Timestamp = now.AddMilliseconds(i)
                    }
                }).GetAwaiter().GetResult();
            }
        });

        sw.Stop();
        var perSecond = (writers * perWriter) / sw.Elapsed.TotalSeconds;
        // 50k/min = ~833/s. We assert 5,000/s — a 6× margin — to keep
        // the test stable under CI noise without losing the regression
        // signal if the lock-free hot path is broken.
        perSecond.Should().BeGreaterThan(5_000,
            $"50k updates/min target requires sustained throughput; achieved {perSecond:F0}/s");
    }

    // ---- GET /deliveries/{id}/tracking SSE -------------------------------------

    [Fact]
    public async Task Tracking_Stream_Emits_Position_Frame_With_Polyline()
    {
        var seed = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8000, dropoffLng: 46.8000);

        // Pre-record a position so the very first SSE frame carries data.
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.7000, Lng = 46.7000, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (eventName, frame) = await ReadFirstSseFrameAsync(http, $"/deliveries/{seed.Id}/tracking", cts.Token);

        eventName.Should().Be("position");
        frame.DeliveryId.Should().Be(seed.Id);
        frame.JeeberId.Should().Be(seed.JeeberId);
        frame.Position.Should().NotBeNull();
        frame.Position!.Lat.Should().Be(24.7000);
        frame.Polyline.Should().HaveCount(2);
        frame.Polyline[0].Should().Equal(new[] { 24.7000, 46.7000 });
        frame.Polyline[1].Should().Equal(new[] { 24.8000, 46.8000 });
        frame.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task Tracking_Stream_Emits_LastSeen_Event_When_Position_Is_Stale()
    {
        // Configure a short stale threshold for this test so we don't
        // wait two minutes. The SSE interval is also tightened so the
        // first frame lands quickly.
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tracking:StaleThreshold"] = "00:00:00.100",
                    ["Tracking:SseInterval"] = "00:00:00.100",
                    ["Tracking:PositionTtl"] = "00:05:00"
                });
            });
        });

        var seed = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8, dropoffLng: 46.8, factory: factory);

        var store = factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.7, Lng = 46.7, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        // Let the recorded fix age past the configured 100ms stale
        // threshold before opening the stream.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", seed.ClientId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber"); // ADR-005 §7 edge user-type
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (eventName, frame) = await ReadFirstSseFrameAsync(http, $"/deliveries/{seed.Id}/tracking", cts.Token);
        eventName.Should().Be("last-seen");
        frame.Stale.Should().BeTrue();
        frame.Position.Should().NotBeNull();
        frame.SecondsSinceUpdate.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Tracking_Stream_Emits_Initial_Frame_With_Null_Position_When_No_Fix()
    {
        var seed = await SeedDeliveryWithDropoffAsync(dropoffLat: 24.8, dropoffLng: 46.8);
        // Do NOT record a position — the stream should still emit the
        // initial "awaiting first ping" frame.

        var http = AuthClient(seed.ClientId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var (eventName, frame) = await ReadFirstSseFrameAsync(http, $"/deliveries/{seed.Id}/tracking", cts.Token);
        eventName.Should().Be("position");
        frame.Position.Should().BeNull();
        frame.Polyline.Should().BeEmpty();
        frame.Stale.Should().BeFalse();
        frame.SecondsSinceUpdate.Should().BeNull();
    }

    [Fact]
    public async Task Tracking_Unknown_Delivery_Returns_404()
    {
        var http = AuthClient("client-x");
        var resp = await http.GetAsync($"/deliveries/missing-{Guid.NewGuid()}/tracking",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Tracking_Non_Participant_Returns_403()
    {
        var seed = await SeedDeliveryWithDropoffAsync(dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient($"stranger-{Guid.NewGuid()}");

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/tracking",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Tracking_Unauthenticated_Returns_401()
    {
        var seed = await SeedDeliveryWithDropoffAsync(dropoffLat: 24.8, dropoffLng: 46.8);
        var http = _factory.CreateClient();

        var resp = await http.GetAsync($"/deliveries/{seed.Id}/tracking",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------------------- helpers ----------------------------------------------

    private HttpClient AuthClient(string userId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        // ADR-005 §7: the trusted edge declares the caller's user type via X-User-Roles. These
        // location routes are §C/§D/§E participant capabilities; a dual-role edge caller satisfies
        // both delivery.track.own ({client}) and delivery.gps.stream ({jeeber}), matching the ADR
        // dual-role-one-token model. The L1 identity the tests rely on is unchanged.
        c.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return c;
    }

    private Task<Seed> SeedDeliveryWithDropoffAsync(
        double dropoffLat,
        double dropoffLng,
        WebApplicationFactory<Program>? factory = null)
    {
        factory ??= _factory;
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        return SeedAsync(store, clientId, jeeberId, dropoffLat, dropoffLng);

        static async Task<Seed> SeedAsync(
            IRequestsStore store, string clientId, string jeeberId, double dropLat, double dropLng)
        {
            var created = await store.CreateAsync(new CreateRequestInput
            {
                ClientId = clientId,
                Description = "Pick up the package",
                DropoffLocation = new GeoPoint { Lat = dropLat, Lng = dropLng }
            }, default);
            var accepted = await store.TryAcceptByJeeberAsync(
                created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
            accepted.Should().NotBeNull();
            await store.SetStatusAsync(created.Id, RequestStatus.PickedUp, default);
            return new Seed(created.Id, clientId, jeeberId);
        }
    }

    /// <summary>
    /// Reads the SSE response one byte at a time until the first
    /// `event: ...\ndata: ...\n\n` block lands, then returns the parsed
    /// frame. Stops the stream by disposing the response so the
    /// controller's loop exits via cancellation.
    /// </summary>
    private static async Task<(string Event, TrackingFrameDto Frame)> ReadFirstSseFrameAsync(
        HttpClient http, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        // S09 (JEB-54): the tracking route now content-negotiates — an explicit
        // Accept: text/event-stream selects the SSE relay; any other Accept gets
        // the one-shot JSON polyline body. The SSE clients these tests exercise
        // declare the stream Accept, matching the live mobile subscription.
        req.Headers.Add("Accept", "text/event-stream");
        var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? eventName = null;
        var dataBuf = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (line.Length == 0)
            {
                if (eventName is not null && dataBuf.Length > 0)
                {
                    var frame = JsonSerializer.Deserialize<TrackingFrameDto>(dataBuf.ToString(), JsonOptions)!;
                    return (eventName, frame);
                }
                continue;
            }
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                eventName = line["event: ".Length..];
            }
            else if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                dataBuf.Append(line["data: ".Length..]);
            }
        }
        throw new InvalidOperationException("SSE stream closed before a complete frame was received.");
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
