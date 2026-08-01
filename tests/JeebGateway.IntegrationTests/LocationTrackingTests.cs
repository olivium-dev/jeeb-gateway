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
///   AC2. GET /deliveries/{id}/tracking returns a one-shot JSON snapshot
///        carrying the latest position and a straight-line polyline to the
///        dropoff. (Was an SSE stream; the 5 s server-side re-read loop that
///        faked it is deleted — see NoBackendPollOrFirestoreListenerGuardTests.)
///   AC3. When the latest fix ages beyond the stale threshold, the snapshot
///        reports <c>stale: true</c> with a <c>secondsSinceUpdate</c>.
///        (Was the stream's <c>last-seen</c> event name.)
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

    // ---- GET /deliveries/{id}/tracking — one-shot snapshot ---------------------

    [Fact]
    public async Task Tracking_Snapshot_Carries_Position_And_Polyline()
    {
        var seed = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8000, dropoffLng: 46.8000);

        // Pre-record a position so the snapshot carries data.
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.7000, Lng = 46.7000, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId);
        var frame = await ReadTrackingSnapshotAsync(http, $"/deliveries/{seed.Id}/tracking");

        frame.DeliveryId.Should().Be(seed.Id);
        frame.JeeberId.Should().Be(seed.JeeberId);
        frame.Position.Should().NotBeNull();
        frame.Position!.Lat.Should().Be(24.7000);
        frame.Polyline.Should().HaveCount(2);
        frame.Polyline[0].Should().Equal(new[] { 24.7000, 46.7000 });
        frame.Polyline[1].Should().Equal(new[] { 24.8000, 46.8000 });
        frame.Stale.Should().BeFalse();
        frame.PositionStatus.Should().Be("live");
    }

    [Fact]
    public async Task Tracking_Snapshot_Reports_Stale_When_Position_Is_Old()
    {
        // Configure a short stale threshold for this test so we don't wait two
        // minutes. There is no interval to shorten any more — the endpoint reads
        // once and returns.
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tracking:StaleThreshold"] = "00:00:00.100",
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

        // Let the recorded fix age past the configured 100ms stale threshold.
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", seed.ClientId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber"); // ADR-005 §7 edge user-type

        var frame = await ReadTrackingSnapshotAsync(http, $"/deliveries/{seed.Id}/tracking");
        frame.Stale.Should().BeTrue();
        frame.Position.Should().NotBeNull("inside PositionTtl the coordinates are still published — a stationary courier legitimately uploads nothing for minutes");
        frame.SecondsSinceUpdate.Should().BeGreaterThan(0);
        frame.PositionStatus.Should().Be("stale");
    }

    [Fact]
    public async Task Tracking_Snapshot_Has_Null_Position_When_No_Fix()
    {
        var seed = await SeedDeliveryWithDropoffAsync(dropoffLat: 24.8, dropoffLng: 46.8);
        // Do NOT record a position — the snapshot must still answer, so the
        // client can paint the "awaiting first ping" state.

        var http = AuthClient(seed.ClientId);
        var frame = await ReadTrackingSnapshotAsync(http, $"/deliveries/{seed.Id}/tracking");
        frame.Position.Should().BeNull();
        frame.Polyline.Should().BeEmpty();
        frame.Stale.Should().BeFalse();
        frame.SecondsSinceUpdate.Should().BeNull();
        frame.PositionStatus.Should().Be("awaitingFirstFix");
    }

    // ---- store-level: TTL classifies, RETENTION forgets -------------------------

    /// <summary>
    /// The two windows must do different jobs. A fix past <c>PositionTtl</c> is
    /// still on record (so "lost" is reportable for the whole trip); a fix past
    /// <c>PositionRetention</c> is genuinely gone (so removing TTL eviction did not
    /// turn the store into an unbounded leak).
    /// </summary>
    [Fact]
    public void Store_Retains_Past_PositionTtl_But_Evicts_Past_PositionRetention()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));
        var options = new TrackingOptions
        {
            StaleThreshold = TimeSpan.FromMinutes(2),
            PositionTtl = TimeSpan.FromMinutes(5),
            PositionRetention = TimeSpan.FromHours(12)
        };
        var store = new InMemoryLocationStore(new TrackingOptionsMonitor(options), clock);

        store.RecordAsync("jeeber-1", new[]
        {
            new GpsPointDto { Lat = 24.7, Lng = 46.7, Accuracy = 5, Timestamp = clock.GetUtcNow() }
        }).GetAwaiter().GetResult();

        // t+301 s — past the 300 s TTL. The fix survives, and the caller classifies
        // it as lost. Before the fix this read returned null AND deleted the entry.
        clock.Advance(TimeSpan.FromSeconds(301));
        var pastTtl = store.GetLatest("jeeber-1");
        pastTtl.Should().NotBeNull("PositionTtl classifies; it must not destroy");
        TrackingFreshness.Classify(pastTtl, clock.GetUtcNow(), options)
            .Should().Be(PositionFreshness.Lost);

        // A second read must be idempotent — the first must not have evicted it.
        store.GetLatest("jeeber-1").Should().NotBeNull("the TTL read is not destructive");

        // t+12 h 1 s — past retention. Now it really is gone.
        clock.Advance(TimeSpan.FromHours(12));
        store.GetLatest("jeeber-1").Should().BeNull(
            "retention still bounds memory, so dropping TTL eviction is not a leak");
    }

    /// <summary>
    /// The ladder <c>StaleThreshold &lt;= PositionTtl &lt; PositionRetention</c> is
    /// enforced at startup, so the defect cannot be reintroduced by configuration
    /// alone. <c>PositionRetention &lt;= PositionTtl</c> means a fix is forgotten
    /// before it can ever be reported as "lost" — which is exactly the collapse
    /// that produced the phantom pin — so the gateway must refuse to boot rather
    /// than serve a wire that silently lies.
    /// </summary>
    [Fact]
    public void Gateway_Refuses_To_Start_When_Retention_Does_Not_Outlast_PositionTtl()
    {
        var factory = _factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tracking:StaleThreshold"] = "00:02:00",
                    ["Tracking:PositionTtl"] = "00:05:00",
                    ["Tracking:PositionRetention"] = "00:05:00" // not > PositionTtl
                });
            });
        });

        // CreateClient() starts the host, which is when ValidateOnStart runs.
        var boot = () => factory.CreateClient();

        boot.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>()
            .WithMessage("*PositionRetention*",
                "the failure must name the option an operator has to fix");
    }

    /// <summary>Minimal <see cref="IOptionsMonitor{T}"/> over a fixed value.</summary>
    private sealed class TrackingOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<TrackingOptions>
    {
        public TrackingOptionsMonitor(TrackingOptions value) => CurrentValue = value;
        public TrackingOptions CurrentValue { get; }
        public TrackingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TrackingOptions, string?> listener) => null;
    }

    // ---- the phantom courier pin -----------------------------------------------

    /// <summary>
    /// REGRESSION — the phantom courier pin.
    ///
    /// <para>A fix that ages past <c>Tracking:PositionTtl</c> must be reported as
    /// <c>lost</c>, with <c>stale:true</c> and a non-null <c>secondsSinceUpdate</c>.
    /// It used to be reported as <c>position:null, stale:false,
    /// secondsSinceUpdate:null</c> — an explicit all-clear — because the store
    /// DELETED the fix on read at the TTL and the controller computed
    /// <c>Stale = latest is not null &amp;&amp; …</c>, which is false by construction
    /// when <c>latest</c> is null. A customer's map keeps its own marker and waits
    /// for <c>stale:true</c> to degrade it, so it left a live-looking pin at a
    /// location the courier had left minutes earlier.</para>
    ///
    /// <para>This drives the PRODUCTION defaults (120 s stale / 300 s TTL) and moves
    /// the gateway's own clock past them rather than shortening the thresholds, so
    /// it reproduces the live capture's arithmetic — a fix 300.836 s old against a
    /// 300.000 s TTL — instead of a scaled-down imitation of it.</para>
    /// </summary>
    [Fact]
    public async Task Tracking_Snapshot_Reports_Lost_When_Fix_Ages_Past_PositionTtl()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });
        var seed = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8, dropoffLng: 46.8, factory: factory);

        var store = factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.7, Lng = 46.7, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        // Sanity: at t0 the position is live and drawn. Without this the test could
        // pass on a snapshot that was never healthy in the first place.
        var http = ClientFor(factory, seed.ClientId);
        var before = await ReadTrackingSnapshotAsync(http, $"/deliveries/{seed.Id}/tracking");
        before.PositionStatus.Should().Be("live");
        before.Position.Should().NotBeNull();
        before.Polyline.Should().HaveCount(2);

        // Age the gateway's own clock past the 300 s default TTL. No sleeps, and no
        // shrunken thresholds: this is the shipped configuration.
        factory.Services
            .GetRequiredService<JeebGateway.TestControlPlane.FakeTimeProvider>()
            .AdvanceBy(TimeSpan.FromSeconds(301));

        var after = await ReadTrackingSnapshotAsync(http, $"/deliveries/{seed.Id}/tracking");

        after.PositionStatus.Should().Be("lost",
            "we had this courier and we no longer know where they are");
        after.Stale.Should().BeTrue(
            "THE defect: this was false — the longer the courier had been missing, the more confidently the wire said 'fine'");
        after.SecondsSinceUpdate.Should().NotBeNull(
            "the age is the evidence that a courier existed and was lost");
        after.SecondsSinceUpdate!.Value.Should().BeGreaterThanOrEqualTo(300);
        after.Position.Should().BeNull(
            "we do not hand out coordinates we cannot vouch for — a client reading only this field must not be able to draw the pin");
        after.Polyline.Should().BeEmpty("no route is drawn from a position we have lost");
        after.Etag.Should().NotBe(before.Etag,
            "a client that skips re-render on an unchanged etag must still learn the courier was lost");
    }

    /// <summary>
    /// The two states must not be byte-identical on the wire. In the live capture
    /// they were: reads taken BEFORE the courier's first fix ever arrived were
    /// indistinguishable from a read where the fix had aged out —
    /// <c>position:null, stale:false, secondsSinceUpdate:null, polyline:[],
    /// etag:cbf29ce484222325</c> (the bare FNV-1a offset basis, i.e. zero
    /// coordinates hashed) in both cases.
    /// </summary>
    [Fact]
    public async Task Tracking_Snapshot_Distinguishes_Courier_Not_Started_From_Courier_Lost()
    {
        var factory = _factory.WithWebHostBuilder(_ => { });

        var neverStarted = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8, dropoffLng: 46.8, factory: factory);
        var lost = await SeedDeliveryWithDropoffAsync(
            dropoffLat: 24.8, dropoffLng: 46.8, factory: factory);

        // Only the second courier ever reports a position.
        var store = factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(lost.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.7, Lng = 46.7, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        factory.Services
            .GetRequiredService<JeebGateway.TestControlPlane.FakeTimeProvider>()
            .AdvanceBy(TimeSpan.FromSeconds(301));

        var a = await ReadTrackingSnapshotAsync(
            ClientFor(factory, neverStarted.ClientId), $"/deliveries/{neverStarted.Id}/tracking");
        var b = await ReadTrackingSnapshotAsync(
            ClientFor(factory, lost.ClientId), $"/deliveries/{lost.Id}/tracking");

        a.PositionStatus.Should().Be("awaitingFirstFix");
        b.PositionStatus.Should().Be("lost");

        // Independent of the new field, the pre-existing fields now differ too, so
        // even a client that never adopts positionStatus can tell them apart.
        a.Stale.Should().BeFalse();
        b.Stale.Should().BeTrue();
        a.SecondsSinceUpdate.Should().BeNull("nothing was ever recorded, so there is no age");
        b.SecondsSinceUpdate.Should().NotBeNull("we know exactly how long ago we lost them");
        a.Etag.Should().NotBe(b.Etag, "the two states used to hash identically");
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

    /// <summary>
    /// <see cref="AuthClient"/> against an explicitly supplied factory — needed by
    /// the tests that take an isolated host so they can advance its clock without
    /// shifting time for every other test in the class.
    /// </summary>
    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return c;
    }

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
    /// Reads the one-shot tracking snapshot. No streaming, no cancellation
    /// token, no held connection: the request completes or the test fails.
    /// </summary>
    private static async Task<TrackingPolylineDto> ReadTrackingSnapshotAsync(HttpClient http, string path)
    {
        var resp = await http.GetAsync(path);
        resp.EnsureSuccessStatusCode();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json",
            "the tracking surface returns a snapshot; the event-stream arm was deleted");
        return (await resp.Content.ReadFromJsonAsync<TrackingPolylineDto>(JsonOptions))!;
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
