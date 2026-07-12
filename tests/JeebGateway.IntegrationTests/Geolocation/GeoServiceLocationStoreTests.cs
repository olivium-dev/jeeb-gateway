using System.Net;
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
/// Gap 1 — unit coverage for <see cref="GeoServiceLocationStore"/>, the
/// upstream-backed <see cref="ILocationStore"/> that delegates to the shared
/// geolocation-service via the NSwag-shaped <see cref="IGeolocationServiceClient"/>.
///
/// These drive the REAL generated client over a stubbed
/// <see cref="HttpMessageHandler"/> so the on-the-wire contract (snake_case body,
/// route shape, 404-as-null) is asserted, not just the C# seam:
///   * write -> read-by-user round trip (POST /location/update, then
///     GET /locations/user/{id});
///   * snake_case wire assertion on the outbound /location/update body (user_id /
///     lat / lng / accuracy / timestamp);
///   * nearest mapping (GET /locations/nearest -> LocationWithDistance);
///   * upstream 404 on read -> null (no fix), not an exception.
/// </summary>
public sealed class GeoServiceLocationStoreTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static GeoServiceLocationStore BuildStore(HttpMessageHandler handler, TrackingOptions? options = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://geo.test/") };
        var client = new GeolocationServiceClient(http);
        var opts = new StaticOptionsMonitor(options ?? new TrackingOptions());
        return new GeoServiceLocationStore(client, opts, TimeProvider.System, NullLogger<GeoServiceLocationStore>.Instance);
    }

    [Fact]
    public async Task Record_Posts_LocationUpdate_And_Maps_Latest()
    {
        string? capturedBody = null;
        string? capturedPath = null;
        var handler = new StubHandler((req, body) =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            capturedBody = body;
            // Upstream LocationUpdateResponse: accepted/rejected/online + latest.
            var json = """
            {
              "accepted": 2,
              "rejected": 0,
              "online": true,
              "latest": { "lat": 24.712, "lng": 46.672, "accuracy": 6.5, "timestamp": "2026-06-13T10:00:00Z" }
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var store = BuildStore(handler);
        var now = DateTimeOffset.Parse("2026-06-13T10:00:00Z");
        var result = await store.RecordAsync("jeeber-1", new[]
        {
            new GpsPointDto { Lat = 24.711, Lng = 46.671, Accuracy = 8.0, Timestamp = now.AddSeconds(-5) },
            new GpsPointDto { Lat = 24.712, Lng = 46.672, Accuracy = 6.5, Timestamp = now },
        });

        capturedPath.Should().Be("/location/update");
        result.Accepted.Should().Be(2);
        result.Rejected.Should().Be(0);
        result.Latest.Should().NotBeNull();
        result.Latest!.Lat.Should().Be(24.712);
        result.Latest.Lng.Should().Be(46.672);

        // snake_case wire assertion: the outbound body uses points[].lat/lng/
        // accuracy/timestamp exactly (FastAPI/Pydantic snake_case), NOT PascalCase.
        capturedBody.Should().NotBeNull();
        using var doc = JsonDocument.Parse(capturedBody!);
        var points = doc.RootElement.GetProperty("points");
        points.GetArrayLength().Should().Be(2);
        var first = points[0];
        first.TryGetProperty("lat", out _).Should().BeTrue("the wire shape is snake_case 'lat'");
        first.TryGetProperty("lng", out _).Should().BeTrue("the wire shape is snake_case 'lng'");
        first.TryGetProperty("accuracy", out _).Should().BeTrue();
        first.TryGetProperty("timestamp", out _).Should().BeTrue();
        first.TryGetProperty("Lat", out _).Should().BeFalse("PascalCase must not leak onto the wire");
    }

    [Fact]
    public async Task GetLatest_Reads_UserLocation_And_Maps_Position()
    {
        string? capturedPath = null;
        var handler = new StubHandler((req, _) =>
        {
            capturedPath = req.RequestUri!.AbsolutePath;
            // Upstream UserLocationResponse: user_id/latitude/longitude/created_at.
            var json = $$"""
            {
              "user_id": "jeeber-1",
              "latitude": 24.72,
              "longitude": 46.68,
              "created_at": "{{DateTimeOffset.UtcNow:O}}"
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var store = BuildStore(handler);
        var latest = await store.GetLatestAsync("jeeber-1");

        capturedPath.Should().Be("/locations/user/jeeber-1");
        latest.Should().NotBeNull();
        latest!.Lat.Should().Be(24.72);
        latest.Lng.Should().Be(46.68);
    }

    [Fact]
    public async Task GetLatest_Returns_Null_On_Upstream_404()
    {
        var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var store = BuildStore(handler);

        var latest = await store.GetLatestAsync("jeeber-unknown");

        latest.Should().BeNull("a 404 from /locations/user/{id} means no fix, not an error");
    }

    [Fact]
    public async Task GetLatest_Returns_Null_When_Upstream_Fix_Is_Older_Than_Ttl()
    {
        var handler = new StubHandler((_, _) =>
        {
            // created_at well beyond the 5-minute TTL.
            var json = $$"""
            {
              "user_id": "jeeber-1",
              "latitude": 24.72,
              "longitude": 46.68,
              "created_at": "{{DateTimeOffset.UtcNow.AddMinutes(-30):O}}"
            }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var store = BuildStore(handler, new TrackingOptions { PositionTtl = TimeSpan.FromMinutes(5) });
        (await store.GetLatestAsync("jeeber-1")).Should().BeNull("a stale upstream fix maps to 'no current fix', matching the in-memory TTL contract");
    }

    [Fact]
    public async Task RecordAsync_Holds_N_Concurrent_Upstream_Calls_Without_Blocking_Threads() // JEBV4-57 AC#3
    {
        // A gated async handler suspends every upstream call until released. With
        // the sync-over-async bridge GONE, all N in-flight RecordAsync calls are
        // truly suspended (awaiting) at once — a blocking bridge would instead pin
        // N thread-pool threads. We launch N from a single caller and assert all N
        // are concurrently in-flight yet none has completed, proving non-blocking.
        using var gate = new SemaphoreSlim(0);
        var inFlight = 0;
        var handler = new AsyncGateHandler(async ct =>
        {
            Interlocked.Increment(ref inFlight);
            await gate.WaitAsync(ct);
            const string json = """
            { "accepted": 1, "rejected": 0, "online": true, "latest": { "lat": 1, "lng": 2, "timestamp": "2026-06-13T10:00:00Z" } }
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var store = BuildStore(handler);
        const int n = 64;
        var point = new[] { new GpsPointDto { Lat = 1, Lng = 2, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow } };

        var tasks = Enumerable.Range(0, n)
            .Select(i => store.RecordAsync($"jeeber-{i}", point))
            .ToArray();

        // Wait until all N upstream calls have entered the (suspended) handler.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref inFlight) < n && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(10);

        Volatile.Read(ref inFlight).Should().Be(n, "all N upstream calls are concurrently in-flight");
        tasks.Should().OnlyContain(t => !t.IsCompleted, "each in-flight call is awaiting, not blocking a thread");

        gate.Release(n);
        var results = await Task.WhenAll(tasks);
        results.Should().OnlyContain(r => r.Accepted == 1);
    }

    [Fact]
    public async Task Generated_Client_GetNearest_Maps_Distance_List()
    {
        string? capturedQuery = null;
        var handler = new StubHandler((req, _) =>
        {
            capturedQuery = req.RequestUri!.PathAndQuery;
            var json = """
            [
              { "id": "loc-1", "user_id": "jeeber-1", "latitude": 24.72, "longitude": 46.68, "created_at": "2026-06-13T10:00:00Z", "tag": null, "distance_km": 0.42 }
            ]
            """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://geo.test/") };
        var client = new GeolocationServiceClient(http);

        var nearest = await client.GetNearestAsync(24.7, 46.6, limit: 5, userId: "jeeber-1");

        capturedQuery.Should().StartWith("/locations/nearest?");
        capturedQuery.Should().Contain("latitude=24.7").And.Contain("longitude=46.6").And.Contain("limit=5").And.Contain("user_id=jeeber-1");
        nearest.Should().HaveCount(1);
        nearest[0].UserId.Should().Be("jeeber-1");
        nearest[0].DistanceKm.Should().Be(0.42);
    }

    // ----- test doubles -----

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, string?, HttpResponseMessage> _responder;

        public StubHandler(Func<HttpRequestMessage, string?, HttpResponseMessage> responder)
            => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request, body);
        }
    }

    private sealed class AsyncGateHandler : HttpMessageHandler
    {
        private readonly Func<CancellationToken, Task<HttpResponseMessage>> _responder;

        public AsyncGateHandler(Func<CancellationToken, Task<HttpResponseMessage>> responder)
            => _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => _responder(cancellationToken);
    }

    private sealed class StaticOptionsMonitor : IOptionsMonitor<TrackingOptions>
    {
        public StaticOptionsMonitor(TrackingOptions value) => CurrentValue = value;
        public TrackingOptions CurrentValue { get; }
        public TrackingOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<TrackingOptions, string?> listener) => null;
    }
}
