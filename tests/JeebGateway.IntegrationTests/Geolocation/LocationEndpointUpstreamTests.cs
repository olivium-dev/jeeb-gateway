using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Geolocation;

/// <summary>
/// Gap 1 — WebApplicationFactory coverage for the Location surface with
/// <c>FeatureFlags:UseUpstream:Geolocation=true</c>. The store swap is invisible
/// to the public route surface (LocationController keeps consuming
/// <see cref="ILocationStore"/>), so these tests prove:
///   * happy-path: POST /location/update succeeds against a stubbed upstream
///     store (the controller records via ILocationStore and returns 200 with the
///     accepted/rejected counts + latest fix);
///   * unauthorized: an anonymous caller (no X-User-Id) gets 401 BEFORE any
///     upstream store call.
///
/// The flag-OFF (in-memory) happy path stays covered by the existing
/// <c>LocationTrackingTests</c>, which run with the default factory.
/// </summary>
public sealed class LocationEndpointUpstreamTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static WebApplicationFactory<Program> UpstreamFactory(ILocationStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Geolocation", "true");
            builder.ConfigureServices(services =>
            {
                // Swap the (upstream-backed) ILocationStore for an in-process stub
                // so the endpoint resolves without a live geolocation-service.
                services.RemoveAll<ILocationStore>();
                services.AddSingleton<ILocationStore>(store);

                // POST /location/update also forwards the latest fix to
                // delivery-service as a presence heartbeat; swap that for the
                // in-process presence fake (same as LocationTrackingTests).
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            });
        });

    [Fact]
    public async Task Update_HappyPath_FlagOn_Records_Via_Upstream_Store_And_Returns_200()
    {
        var store = new RecordingLocationStore();
        using var factory = UpstreamFactory(store);

        var http = factory.CreateClient();
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        http.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");

        var now = DateTimeOffset.UtcNow;
        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.7100, lng = 46.6700, accuracy = 10.0, timestamp = now.AddSeconds(-5) },
                new { lat = 24.7120, lng = 46.6720, accuracy = 6.5, timestamp = now },
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(2);
        body.Latest!.Lat.Should().Be(24.7120);

        // The upstream-backed store seam was actually hit by the controller.
        store.RecordedJeeberId.Should().Be(jeeberId);
        store.RecordedPointCount.Should().Be(2);
    }

    [Fact]
    public async Task Update_FlagOn_Unauthenticated_Returns_401_Without_Hitting_Store()
    {
        var store = new RecordingLocationStore();
        using var factory = UpstreamFactory(store);

        // No X-User-Id header -> anonymous.
        var http = factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new object[]
            {
                new { lat = 24.71, lng = 46.67, timestamp = DateTimeOffset.UtcNow },
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        store.RecordedJeeberId.Should().BeNull("the 401 must short-circuit before any store call");
    }

    /// <summary>
    /// Stand-in <see cref="ILocationStore"/> that records the controller's
    /// invocation and returns a deterministic latest fix from the last point, so
    /// the endpoint test asserts the controller↔store contract without a live
    /// upstream. (The upstream wire mapping itself is covered by
    /// <see cref="GeoServiceLocationStoreTests"/>.)
    /// </summary>
    private sealed class RecordingLocationStore : ILocationStore
    {
        public string? RecordedJeeberId { get; private set; }
        public int RecordedPointCount { get; private set; }

        public LocationStoreUpdateResult Record(string jeeberId, IReadOnlyList<GpsPointDto> points)
        {
            RecordedJeeberId = jeeberId;
            RecordedPointCount = points.Count;
            var newest = points[^1];
            var latest = new StoredPosition(newest.Lat, newest.Lng, newest.Accuracy, newest.Timestamp, DateTimeOffset.UtcNow);
            return new LocationStoreUpdateResult(points.Count, 0, latest);
        }

        public StoredPosition? GetLatest(string jeeberId) =>
            RecordedJeeberId == jeeberId && RecordedPointCount > 0
                ? new StoredPosition(0, 0, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                : null;
    }
}
