using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Generated.GeolocationService;
using JeebGateway.Tracking;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Geolocation;

/// <summary>
/// D11 (Phase V run 2, 2026-08-16) — the courier marker never rendered.
///
/// <para>The jeeber's <c>POST /location/update</c> returned
/// <c>{accepted:1, latest:{…}}</c> with the exact coordinates echoed, and 82 seconds
/// later the client's <c>GET /deliveries/{id}/tracking</c> still answered
/// <c>position:null, positionStatus:awaitingFirstFix</c> with an unchanged etag. Two
/// server-side views of one delivery disagreed because the two halves of
/// <see cref="ILocationStore"/> were pointed at DIFFERENT TABLES inside
/// geolocation-service:</para>
/// <list type="bullet">
///   <item>write — <c>POST /location/update</c> derives the subject from the forwarded
///     bearer and calls <c>bump_presence</c>, writing <c>user_status</c>;</item>
///   <item>read — <c>GET /locations/user/{id}</c> serves <c>locations</c>, a table written
///     only by <c>POST /locations</c> (a tagged saved-place upsert) and therefore
///     provably EMPTY on live.</item>
/// </list>
///
/// <para><b>Why the old suite passed anyway.</b> Every existing read test stubs a bare
/// <see cref="HttpMessageHandler"/> that answers 200 with a hand-written
/// <c>UserLocationResponse</c> no matter which path is asked. That asserts the gateway
/// can parse a body it was handed; it cannot notice that no writer ever produces that
/// body. The check could not fail. So the fake below is STATEFUL: it routes, it stores,
/// and the only way <c>GetLatestAsync</c> can return a fix is if a real
/// <c>RecordAsync</c> put one somewhere this read can reach.</para>
/// </summary>
public sealed class GeoStoreWriteReadSameRungTests
{
    private const string JeeberId = "5ae06873-25b6-466d-bc09-69b402570e7d";

    /// <summary>
    /// THE DEFECT. Record a fix through the real client, then read it back through the
    /// real client, against one upstream that behaves like geolocation-service. Red
    /// while the read asks <c>/locations/user/{id}</c>; green once it asks the presence
    /// row the write actually bumped.
    /// </summary>
    [Fact]
    public async Task Fix_Recorded_Through_The_Write_Path_Is_Visible_To_The_Read_Path()
    {
        var upstream = new FakeGeolocationService();
        var store = BuildStore(upstream, JeeberId);
        var at = DateTimeOffset.Parse("2026-08-16T13:05:00.544Z");

        var recorded = await store.RecordAsync(JeeberId, new[]
        {
            new GpsPointDto { Lat = 52.3994997, Lng = 5.2751361, Accuracy = 11.639, Timestamp = at },
        });

        recorded.Accepted.Should().Be(1, "the write half was never the broken half");
        recorded.Latest.Should().NotBeNull();

        var latest = await store.GetLatestAsync(JeeberId);

        latest.Should().NotBeNull(
            "an accepted fix must be readable by the tracking snapshot — this null is D11: "
            + "position:null / awaitingFirstFix while the server holds the fix");
        latest!.Lat.Should().Be(52.3994997);
        latest.Lng.Should().Be(5.2751361);
    }

    /// <summary>
    /// The read must land on the presence row, by route. Asserting the coordinates alone
    /// would still pass if someone re-added a second lookup as a "fallback", which is how
    /// a store ends up reading a table nothing writes in the first place.
    /// </summary>
    [Fact]
    public async Task Read_Path_Asks_The_Presence_Row_The_Write_Bumps()
    {
        var upstream = new FakeGeolocationService();
        var store = BuildStore(upstream, JeeberId);

        await store.RecordAsync(JeeberId, new[]
        {
            new GpsPointDto { Lat = 52.3994997, Lng = 5.2751361, Timestamp = DateTimeOffset.UtcNow },
        });
        upstream.Paths.Clear();
        await store.GetLatestAsync(JeeberId);

        upstream.Paths.Should().ContainSingle().Which
            .Should().Be($"/v1/geo/agents/{JeeberId}/availability");
    }

    /// <summary>
    /// NEGATIVE CONTROL #1 — the fake is not rigged to say "found". A jeeber who never
    /// uploaded has no presence row, and the store reports the absence as null. If this
    /// failed, the test above would be proving nothing.
    /// </summary>
    [Fact]
    public async Task Jeeber_Who_Never_Uploaded_Reads_As_No_Fix()
    {
        var upstream = new FakeGeolocationService();
        var store = BuildStore(upstream, JeeberId);

        (await store.GetLatestAsync("jeeber-who-never-moved")).Should().BeNull();
    }

    /// <summary>
    /// NEGATIVE CONTROL #2 — a presence row WITHOUT coordinates (the jeeber toggled
    /// availability but has never streamed a fix) must still read as null. This is the
    /// one case where <c>awaitingFirstFix</c> is the honest answer, and the fix must not
    /// turn it into a phantom pin at (0,0).
    /// </summary>
    [Fact]
    public async Task Presence_Row_Without_Coordinates_Reads_As_No_Fix()
    {
        var upstream = new FakeGeolocationService();
        upstream.SetAvailabilityWithoutFix(JeeberId);
        var store = BuildStore(upstream, JeeberId);

        (await store.GetLatestAsync(JeeberId)).Should().BeNull(
            "a row with no lat/lng is genuinely 'no fix on record', not a fix at the equator");
    }

    /// <summary>
    /// NEGATIVE CONTROL #3 — the fake reproduces the LIVE asymmetry rather than a
    /// convenient one: after an accepted batch, <c>GET /locations/user/{id}</c> is still
    /// a 404, exactly as the live service answers for the Phase V jeeber while
    /// <c>/v1/geo/agents/{id}/availability</c> returns his fix. If the ingest ever starts
    /// co-writing <c>locations</c> upstream, this test fails and says so.
    /// </summary>
    [Fact]
    public async Task Ingest_Does_Not_Populate_The_Generic_Locations_Table()
    {
        var upstream = new FakeGeolocationService();
        var store = BuildStore(upstream, JeeberId);
        var client = new GeolocationServiceClient(BuildHttp(upstream, JeeberId));

        await store.RecordAsync(JeeberId, new[]
        {
            new GpsPointDto { Lat = 52.3994997, Lng = 5.2751361, Timestamp = DateTimeOffset.UtcNow },
        });

        (await client.GetUserLocationAsync(JeeberId)).Should().BeNull(
            "POST /location/update writes user_status, never the locations table — "
            + "that gap is the whole of D11");
        (await client.GetAgentPresenceAsync(JeeberId)).Should().NotBeNull(
            "…and the same fix IS on the presence row");
    }

    /// <summary>
    /// Age survives the rung change: a fix older than <c>PositionTtl</c> is returned with
    /// its stamp intact so <see cref="TrackingFreshness"/> can call it lost. Swallowing it
    /// here is the phantom-pin defect (#342) and must not come back through the new route.
    /// </summary>
    [Fact]
    public async Task Old_Fix_Is_Returned_With_Its_Age_Intact()
    {
        var upstream = new FakeGeolocationService();
        var lastSeen = DateTimeOffset.UtcNow.AddMinutes(-30);
        upstream.SeedPresence(JeeberId, 52.3994997, 5.2751361, lastSeen);
        var options = new TrackingOptions { PositionTtl = TimeSpan.FromMinutes(5) };
        var store = BuildStore(upstream, JeeberId, options);

        var latest = await store.GetLatestAsync(JeeberId);

        latest.Should().NotBeNull();
        latest!.ReceivedAt.Should().BeCloseTo(lastSeen, TimeSpan.FromSeconds(1));
        TrackingFreshness.Classify(latest, DateTimeOffset.UtcNow, options)
            .Should().Be(PositionFreshness.Lost);
    }

    /// <summary>Past retention the fix is forgotten entirely, on the new route too.</summary>
    [Fact]
    public async Task Fix_Older_Than_Retention_Is_Forgotten()
    {
        var upstream = new FakeGeolocationService();
        upstream.SeedPresence(JeeberId, 52.3994997, 5.2751361, DateTimeOffset.UtcNow.AddDays(-3));
        var store = BuildStore(upstream, JeeberId, new TrackingOptions
        {
            PositionTtl = TimeSpan.FromMinutes(5),
            PositionRetention = TimeSpan.FromHours(12),
        });

        (await store.GetLatestAsync(JeeberId)).Should().BeNull();
    }

    private static GeoServiceLocationStore BuildStore(
        FakeGeolocationService upstream, string subject, TrackingOptions? options = null)
    {
        var client = new GeolocationServiceClient(BuildHttp(upstream, subject));
        return new GeoServiceLocationStore(
            client,
            new StaticOptionsMonitor(options ?? new TrackingOptions()),
            TimeProvider.System,
            NullLogger<GeoServiceLocationStore>.Instance);
    }

    private static HttpClient BuildHttp(FakeGeolocationService upstream, string subject)
    {
        var http = new HttpClient(upstream) { BaseAddress = new Uri("http://geo.test/") };
        // Mirrors BearerForwardingHandler: the jeeber's own bearer travels downstream,
        // which is why the upstream subject IS the jeeber id (confirmed on live).
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"{subject}:agent");
        return http;
    }

    /// <summary>
    /// A stateful stand-in for geolocation-service. It keeps the two stores the real
    /// service keeps and wires each route to the one the real service wires it to:
    /// <c>POST /location/update</c> -> presence (<c>user_status</c>, keyed by the bearer
    /// subject); <c>GET /v1/geo/agents/{id}/availability</c> -> presence;
    /// <c>GET /locations/user/{id}</c> -> locations, which only <c>POST /locations</c>
    /// fills. Nothing here is a canned 200.
    /// </summary>
    private sealed class FakeGeolocationService : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ConcurrentDictionary<string, Presence> _presence = new();
        private readonly ConcurrentDictionary<string, Presence> _locations = new();

        public List<string> Paths { get; } = new();

        public void SeedPresence(string userId, double lat, double lng, DateTimeOffset lastSeen)
            => _presence[userId] = new Presence(lat, lng, lastSeen);

        public void SetAvailabilityWithoutFix(string userId)
            => _presence[userId] = new Presence(null, null, DateTimeOffset.UtcNow);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (Paths) Paths.Add(path);

            if (request.Method == HttpMethod.Post && path == "/location/update")
            {
                return Task.FromResult(Ingest(request, ct));
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/v1/geo/agents/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/v1/geo/agents/".Length..].Replace("/availability", string.Empty));
                if (!_presence.TryGetValue(id, out var row)) return Task.FromResult(NotFound());
                return Task.FromResult(Ok(new
                {
                    user_id = id,
                    available = false,
                    last_seen_at = row.LastSeen,
                    updated_at = row.LastSeen,
                    latitude = row.Lat,
                    longitude = row.Lng,
                    location_geohash5 = row.Lat is null ? null : "u17f0",
                    role = "agent",
                    reason = (string?)null,
                }));
            }

            if (request.Method == HttpMethod.Get && path.StartsWith("/locations/user/", StringComparison.Ordinal))
            {
                var id = Uri.UnescapeDataString(path["/locations/user/".Length..]);
                if (!_locations.TryGetValue(id, out var row) || row.Lat is null) return Task.FromResult(NotFound());
                return Task.FromResult(Ok(new
                {
                    user_id = id,
                    latitude = row.Lat,
                    longitude = row.Lng,
                    created_at = row.LastSeen,
                }));
            }

            return Task.FromResult(NotFound());
        }

        /// <summary>
        /// POST /location/update: subject from the bearer (401 otherwise), newest point by
        /// device timestamp wins, presence bumped, echo back. The locations table is not
        /// touched — because upstream does not touch it.
        /// </summary>
        private HttpResponseMessage Ingest(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.Authorization?.Parameter;
            var subject = token is null ? null : token.Split(':') is [var s, "agent" or "client" or "admin"] ? s : null;
            if (string.IsNullOrEmpty(subject))
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            var body = request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(body);
            var points = doc.RootElement.GetProperty("points");
            var newest = points.EnumerateArray()
                .OrderBy(p => p.TryGetProperty("timestamp", out var t) && t.ValueKind != JsonValueKind.Null
                    ? t.GetDateTimeOffset()
                    : DateTimeOffset.MinValue)
                .Last();

            var lat = newest.GetProperty("lat").GetDouble();
            var lng = newest.GetProperty("lng").GetDouble();
            _presence[subject] = new Presence(lat, lng, DateTimeOffset.UtcNow);

            return Ok(new
            {
                accepted = points.GetArrayLength(),
                rejected = 0,
                online = false,
                latest = new { lat, lng, accuracy = (double?)null, timestamp = (DateTimeOffset?)null },
            });
        }

        private static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent("""{"detail":"not found"}""", Encoding.UTF8, "application/json"),
        };

        private static HttpResponseMessage Ok(object payload) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json"),
        };

        private sealed record Presence(double? Lat, double? Lng, DateTimeOffset LastSeen);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<TrackingOptions>
    {
        public StaticOptionsMonitor(TrackingOptions value) => CurrentValue = value;
        public TrackingOptions CurrentValue { get; }
        public TrackingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TrackingOptions, string?> listener) => null;
    }
}
