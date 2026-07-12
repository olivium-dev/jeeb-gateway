using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tracking;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S09 (JEB-54) gateway BFF surface for live tracking + settlement read:
///
///   PR-3a. GET /v1/geo/jeeb/stream/{id} — participant-gated SSE alias.
///          Party ⇒ 200 text/event-stream (H3/A1/A2); non-party ⇒ 403 (N1).
///   PR-3b. GET /deliveries/{id}/tracking with a non-SSE Accept ⇒ JSON polyline
///          body carrying $.polyline + $.etag (H4/A3).
///   PR-4.  POST /location/update {deliveryId,lat,lng} — delivery-scoped ingest
///          authz: non-party ⇒ 403 (N2); not-in-transit ⇒ 409 (N5); the bound
///          jeeber while in-transit ⇒ 200.
///   PR-5.  GET /v1/deliveries/{id}/settlement — settlement-intent read: window
///          open at Done ⇒ 200 Created=true (H8); not a party ⇒ 403; unknown ⇒ 404.
///
/// These run on the local-mirror path (FeatureFlags:UseUpstream:Delivery OFF,
/// the default) so the IRequestsStore seed is authoritative; the canonical
/// read-through is covered by the existing DeliveriesEndpointTests fakes.
/// </summary>
public class S09TrackingSettlementBffTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public S09TrackingSettlementBffTests(WebApplicationFactory<Program> factory)
    {
        // Same swap as LocationTrackingTests: the GPS ingest path heartbeats to
        // delivery-service; the in-process presence fake resolves it without a
        // live Go upstream. The mirror-path resolver never calls the canonical
        // read, so the fake's NotSupported canonical method is never hit.
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryPresenceClient());
            });
        });
    }

    // ---- PR-3a: SSE alias /v1/geo/jeeb/stream/{id} ------------------------------

    [Fact] // H3 / A1 / A2
    public async Task SseAlias_Participant_Opens_EventStream()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.70, Lng = 46.70, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (eventName, frame) = await ReadFirstSseFrameAsync(
            http, $"/v1/geo/jeeb/stream/{seed.Id}", cts.Token);

        eventName.Should().Be("position");
        frame.DeliveryId.Should().Be(seed.Id);
        frame.Position.Should().NotBeNull();
        frame.Polyline.Should().HaveCount(2);
    }

    [Fact] // N1: Rana removed at S07-accept is not in {Sami, Kamal, admin}
    public async Task SseAlias_NonParticipant_Returns_403_ProblemJson()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient($"rana-{Guid.NewGuid()}");

        var resp = await http.GetAsync($"/v1/geo/jeeb/stream/{seed.Id}",
            HttpCompletionOption.ResponseHeadersRead);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact] // A1: admin live-ops gets a participant-equivalent view
    public async Task SseAlias_Admin_Authorized_As_Participant()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", $"admin-{Guid.NewGuid()}");
        http.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var (eventName, _) = await ReadFirstSseFrameAsync(
            http, $"/v1/geo/jeeb/stream/{seed.Id}", cts.Token);
        eventName.Should().Be("position");
    }

    [Fact]
    public async Task SseAlias_Unauthenticated_Returns_401()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = _factory.CreateClient();
        var resp = await http.GetAsync($"/v1/geo/jeeb/stream/{seed.Id}",
            HttpCompletionOption.ResponseHeadersRead);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---- PR-3b: content-negotiated polyline body --------------------------------

    [Fact] // H4 / A3
    public async Task Tracking_NonSse_Accept_Returns_Json_Polyline_With_Etag()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.80, dropoffLng: 46.80);
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.70, Lng = 46.70, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId); // no Accept: text/event-stream
        var resp = await http.GetAsync($"/deliveries/{seed.Id}/tracking");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var body = await resp.Content.ReadFromJsonAsync<TrackingPolylineDto>(JsonOptions);
        body!.Polyline.Should().HaveCount(2);
        body.Polyline[0].Should().Equal(new[] { 24.70, 46.70 });
        body.Polyline[1].Should().Equal(new[] { 24.80, 46.80 });
        body.Etag.Should().NotBeNullOrEmpty();
        body.Position.Should().NotBeNull();
    }

    [Fact] // A3: a repeat read returns a stable etag for the same geometry
    public async Task Tracking_Polyline_Etag_Is_Stable_For_Same_Route()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.80, dropoffLng: 46.80);
        var store = _factory.Services.GetRequiredService<ILocationStore>();
        await store.RecordAsync(seed.JeeberId, new[]
        {
            new GpsPointDto { Lat = 24.70, Lng = 46.70, Accuracy = 5, Timestamp = DateTimeOffset.UtcNow }
        });

        var http = AuthClient(seed.ClientId);
        var first = await (await http.GetAsync($"/deliveries/{seed.Id}/tracking"))
            .Content.ReadFromJsonAsync<TrackingPolylineDto>(JsonOptions);
        var second = await (await http.GetAsync($"/deliveries/{seed.Id}/tracking"))
            .Content.ReadFromJsonAsync<TrackingPolylineDto>(JsonOptions);

        second!.Etag.Should().Be(first!.Etag);
    }

    [Fact]
    public async Task Tracking_Polyline_NonParticipant_Returns_403()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient($"stranger-{Guid.NewGuid()}");
        var resp = await http.GetAsync($"/deliveries/{seed.Id}/tracking");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ---- PR-4: delivery-scoped location ingest authz ----------------------------

    [Fact] // N2: a non-party Jeeber is denied before any gps_pings write
    public async Task LocationUpdate_Scoped_NonParticipant_Returns_403()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient($"outsider-{Guid.NewGuid()}");

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            deliveryId = seed.Id,
            lat = 33.9,
            lng = 35.5
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // N5/E4: a ping before/after the en-route phase is refused with 409
    public async Task LocationUpdate_Scoped_NotInTransit_Returns_409()
    {
        // AtDoor — past the en-route phase: ingest gate has closed.
        var seed = await SeedAsync(status: RequestStatus.AtDoor, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient(seed.JeeberId);

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            deliveryId = seed.Id,
            lat = 33.9,
            lng = 35.5
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact] // happy: the bound jeeber, in-transit, ingests one fix
    public async Task LocationUpdate_Scoped_BoundJeeber_InTransit_Returns_200_And_Records()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient(seed.JeeberId);

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            deliveryId = seed.Id,
            lat = 24.71,
            lng = 46.71,
            accuracy = 7.0
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(1);
        body.Latest!.Lat.Should().Be(24.71);

        var store = _factory.Services.GetRequiredService<ILocationStore>();
        (await store.GetLatestAsync(seed.JeeberId))!.Lng.Should().Be(46.71);
    }

    [Fact] // legacy batch shape (no deliveryId) is unchanged — no gate
    public async Task LocationUpdate_Legacy_Batch_No_DeliveryId_Still_Works()
    {
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var http = AuthClient(jeeberId);

        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            points = new[]
            {
                new { lat = 24.71, lng = 46.71, accuracy = (double?)5.0, timestamp = DateTimeOffset.UtcNow }
            }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LocationUpdateResponse>(JsonOptions);
        body!.Accepted.Should().Be(1);
    }

    [Fact] // scoped against an unknown delivery ⇒ 404
    public async Task LocationUpdate_Scoped_Unknown_Delivery_Returns_404()
    {
        var http = AuthClient($"jeeber-{Guid.NewGuid()}");
        var resp = await http.PostAsJsonAsync("/location/update", new
        {
            deliveryId = $"missing-{Guid.NewGuid()}",
            lat = 24.71,
            lng = 46.71
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ---- PR-5: settlement-intent read -------------------------------------------

    [Fact] // H8: window open at Done ⇒ 200 Created=true, idempotent on deliveryId
    public async Task Settlement_Read_WindowOpen_At_Delivered_Returns_Created()
    {
        var seed = await SeedAsync(status: RequestStatus.Delivered, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient(seed.JeeberId); // Kamal reads as the bound jeeber

        var resp = await http.GetAsync($"/v1/deliveries/{seed.Id}/settlement");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SettlementIntentResponse>(JsonOptions);
        body!.DeliveryId.Should().Be(seed.Id);
        body.Created.Should().BeTrue();
        body.State.Should().Be("pending_settlement");

        // Idempotent: a repeat read yields the same intent (no double-create).
        var again = await (await http.GetAsync($"/v1/deliveries/{seed.Id}/settlement"))
            .Content.ReadFromJsonAsync<SettlementIntentResponse>(JsonOptions);
        again!.Created.Should().BeTrue();
        again.State.Should().Be("pending_settlement");
    }

    [Fact] // not yet at a settle-able state ⇒ 200 but not created
    public async Task Settlement_Read_Before_Done_Returns_NotReady()
    {
        var seed = await SeedAsync(status: RequestStatus.HeadingOff, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient(seed.JeeberId);

        var body = await (await http.GetAsync($"/v1/deliveries/{seed.Id}/settlement"))
            .Content.ReadFromJsonAsync<SettlementIntentResponse>(JsonOptions);
        body!.Created.Should().BeFalse();
        body.State.Should().Be("not_ready");
    }

    [Fact]
    public async Task Settlement_Read_NonParticipant_Returns_403()
    {
        var seed = await SeedAsync(status: RequestStatus.Delivered, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = AuthClient($"stranger-{Guid.NewGuid()}");
        var resp = await http.GetAsync($"/v1/deliveries/{seed.Id}/settlement");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Settlement_Read_Unknown_Delivery_Returns_404()
    {
        var http = AuthClient($"jeeber-{Guid.NewGuid()}");
        var resp = await http.GetAsync($"/v1/deliveries/missing-{Guid.NewGuid()}/settlement");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Settlement_Read_Unauthenticated_Returns_401()
    {
        var seed = await SeedAsync(status: RequestStatus.Delivered, dropoffLat: 24.8, dropoffLng: 46.8);
        var http = _factory.CreateClient();
        var resp = await http.GetAsync($"/v1/deliveries/{seed.Id}/settlement");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -------------------- helpers ----------------------------------------------

    private HttpClient AuthClient(string userId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Dual-role edge caller satisfies both delivery.track.own ({client}) and
        // delivery.gps.stream ({jeeber}) — the S09 routes are §C/§D/§E participant
        // capabilities (ADR-005 §7 dual-role-one-token).
        c.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return c;
    }

    private async Task<Seed> SeedAsync(string status, double dropoffLat, double dropoffLng)
    {
        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            DropoffLocation = new GeoPoint { Lat = dropoffLat, Lng = dropoffLng }
        }, default);
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        await store.SetStatusAsync(created.Id, status, default);
        return new Seed(created.Id, clientId, jeeberId);
    }

    private static async Task<(string Event, TrackingFrameDto Frame)> ReadFirstSseFrameAsync(
        HttpClient http, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
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
