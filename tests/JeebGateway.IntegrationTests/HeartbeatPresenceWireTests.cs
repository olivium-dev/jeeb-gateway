using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S06 / ADR-HB-001 — gateway availability cutover to the NEW reusable
/// <c>heart-beat</c> presence service (flag-gated, additive).
///
/// <para>
/// These tests assert two things:
/// </para>
/// <list type="number">
///   <item>
///     With <c>FeatureFlags:Heartbeat:Enabled=true</c>, the GET/PATCH
///     <c>/jeebers/me/availability</c> surface routes through the real
///     <see cref="HeartBeatServiceClient"/> over a stub HTTP handler — so the
///     assertions cover the actual HTTP path (<c>PATCH /v1/presence</c>,
///     <c>GET /v1/presence/{userId}</c>), the <b>camelCase</b> body field names,
///     and the response binding. The public <see cref="AvailabilityResponse"/>
///     shape is byte-identical to the delivery-service path, so no S06 assertion
///     shifts.
///   </item>
///   <item>
///     The N13 fix: a successful offline toggle never 500s even when the
///     gateway-local in-memory mirror (<see cref="IAvailabilityStore"/>
///     <c>GoOfflineAsync</c>, which fans out withdraw-offer side-effects) throws.
///     This is verified on BOTH the default (delivery) path and the heart-beat
///     path, since the best-effort wrap is flag-independent.
///   </item>
/// </list>
///
/// <para>
/// The DEFAULT (flag off) heart-beat-inert behaviour — GET/PATCH still routing
/// through delivery-service unchanged — is already covered by
/// <see cref="PresenceThinWireTests"/>, which runs with no flag override.
/// </para>
/// </summary>
public class HeartbeatPresenceWireTests
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    // -------- PATCH (online) routes through heart-beat with camelCase ----------

    [Fact]
    public async Task FlagOn_Patch_Online_Patches_To_HeartBeat_Presence_With_CamelCase_Body()
    {
        var captured = new List<HttpRequestMessage>();
        var bodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            if (req.Content is not null) bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"userId":"jeeber-1","online":true,"lastSeenAt":"2026-06-08T12:00:00Z",
                 "wentOnlineAt":"2026-06-08T12:00:00Z","roleKey":"jeeber",
                 "lat":31.9539,"lng":35.9106,"updatedAt":"2026-06-08T12:00:00Z"}
                """);
        });

        using var factory = NewHeartbeatFactory(s => ReplaceHeartBeatClient(s, stub));
        var client = JeeberClient(factory, "jeeber-1");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "motorbike",
            zone = "amman-downtown",
            latitude = 31.9539,
            longitude = 35.9106
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The online write hit the heart-beat presence toggle (PATCH /v1/presence).
        var setReq = captured.Single(r =>
            r.Method == HttpMethod.Patch &&
            r.RequestUri!.AbsolutePath == "/v1/presence");
        setReq.Should().NotBeNull();

        var sent = bodies.Single();
        sent.Should().Contain("\"userId\":\"jeeber-1\"");
        sent.Should().Contain("\"online\":true");
        sent.Should().Contain("\"roleKey\":\"jeeber\"");   // opaque namespace, camelCase
        sent.Should().Contain("\"lat\":31.9539");
        sent.Should().Contain("\"lng\":35.9106");

        // GOVERNANCE: no Jeeb domain language crosses the heart-beat wire — the
        // toggle's vehicleType/zone are Jeeb semantics the gateway keeps; they must
        // not appear in the presence body.
        sent.Should().NotContain("vehicle");
        sent.Should().NotContain("zone");

        // The camelCase heart-beat response binds onto the public response shape,
        // with the Jeeb-semantic vehicle/zone echoed back from the request.
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.UserId.Should().Be("jeeber-1");
        body.Online.Should().BeTrue();
        body.VehicleType.Should().Be("motorbike");
        body.Zone.Should().Be("amman-downtown");
        body.Latitude.Should().Be(31.9539);
        body.Longitude.Should().Be(35.9106);
    }

    // -------- PATCH (offline, N13 home) routes through heart-beat --------------

    [Fact]
    public async Task FlagOn_Patch_Offline_Patches_Offline_To_HeartBeat_And_Returns_200()
    {
        var bodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            if (req.Content is not null) bodies.Add(req.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"userId":"jeeber-off","online":false,"updatedAt":"2026-06-08T12:05:00Z"}
                """);
        });

        using var factory = NewHeartbeatFactory(s => ReplaceHeartBeatClient(s, stub));
        var client = JeeberClient(factory, "jeeber-off");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        bodies.Single().Should().Contain("\"online\":false");

        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.Online.Should().BeFalse();
    }

    // -------- GET routes through heart-beat -----------------------------------

    [Fact]
    public async Task FlagOn_Get_Reads_From_HeartBeat_And_Maps_Response()
    {
        var captured = new List<HttpRequestMessage>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(
                """
                {"userId":"jeeber-g","online":true,"lat":31.96,"lng":35.90,
                 "lastSeenAt":"2026-06-08T12:00:00Z","updatedAt":"2026-06-08T12:00:00Z"}
                """);
        });

        using var factory = NewHeartbeatFactory(s => ReplaceHeartBeatClient(s, stub));
        var client = JeeberClient(factory, "jeeber-g");

        var resp = await client.GetAsync("/jeebers/me/availability");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        captured.Should().Contain(r =>
            r.Method == HttpMethod.Get &&
            r.RequestUri!.AbsolutePath == "/v1/presence/jeeber-g");

        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.UserId.Should().Be("jeeber-g");
        body.Online.Should().BeTrue();
        body.Latitude.Should().Be(31.96);
    }

    [Fact]
    public async Task FlagOn_Get_When_Never_Online_Returns_Offline_Default_Not_500()
    {
        // heart-beat 404 (no presence row) → offline default, never a 500.
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("", Encoding.UTF8, "application/json")
            });

        using var factory = NewHeartbeatFactory(s => ReplaceHeartBeatClient(s, stub));
        var client = JeeberClient(factory, "jeeber-new");

        var resp = await client.GetAsync("/jeebers/me/availability");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.UserId.Should().Be("jeeber-new");
        body.Online.Should().BeFalse();
        body.Zone.Should().BeNull();
    }

    // -------- N13 fix: best-effort offline mirror never 500s ------------------

    [Fact]
    public async Task N13_Offline_Does_Not_500_When_InMemory_Mirror_Throws_DeliveryPath()
    {
        // DEFAULT (heart-beat off) path: the authoritative offline write hits
        // delivery-service and succeeds; the gateway-local GoOfflineAsync mirror
        // throws (e.g. a withdraw-offer fan-out failure). Pre-fix this 500'd
        // (N13). Now the toggle still returns 200 with 0 withdrawn offers.
        var deliveryStub = new StubHttpMessageHandler(_ => JsonResponse(
            """{"jeeber_id":"jeeber-n13","online":false,"updated_at":"2026-06-08T12:05:00Z"}"""));

        using var factory = NewFactory(s =>
        {
            ReplaceDeliveryClient(s, deliveryStub);
            ReplaceStoreWithThrowingOfflineMirror(s);
        });
        var client = JeeberClient(factory, "jeeber-n13");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.Online.Should().BeFalse();
        body.WithdrawnOffers.Should().Be(0, "a failed best-effort mirror degrades the withdrawn count to 0, it does not 500");
    }

    [Fact]
    public async Task N13_Offline_Does_Not_500_When_InMemory_Mirror_Throws_HeartBeatPath()
    {
        // heart-beat-on path: same N13 guarantee. The authoritative offline write
        // hits heart-beat and succeeds; the local mirror throws; the toggle still
        // returns 200.
        var heartBeatStub = new StubHttpMessageHandler(_ => JsonResponse(
            """{"userId":"jeeber-n13-hb","online":false,"updatedAt":"2026-06-08T12:05:00Z"}"""));

        using var factory = NewHeartbeatFactory(s =>
        {
            ReplaceHeartBeatClient(s, heartBeatStub);
            ReplaceStoreWithThrowingOfflineMirror(s);
        });
        var client = JeeberClient(factory, "jeeber-n13-hb");

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AvailabilityResponse>(JsonOpts);
        body!.Online.Should().BeFalse();
        body.WithdrawnOffers.Should().Be(0);
    }

    // -------------------------------------------------------------------------
    // Helpers (mirror PresenceThinWireTests' boundary-stub pattern)
    // -------------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(Action<IServiceCollection>? configure = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configure is not null) builder.ConfigureTestServices(configure);
        });

    private static WebApplicationFactory<Program> NewHeartbeatFactory(Action<IServiceCollection>? configure = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:Heartbeat:Enabled", "true");
            if (configure is not null) builder.ConfigureTestServices(configure);
        });

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }

    private static void ReplaceHeartBeatClient(IServiceCollection services, HttpMessageHandler handler)
    {
        services.RemoveAll<IHeartBeatServiceClient>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upstream-heartbeat.test") };
        services.AddSingleton<IHeartBeatServiceClient>(new HeartBeatServiceClient(http));
    }

    private static void ReplaceDeliveryClient(IServiceCollection services, HttpMessageHandler handler)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://upstream-delivery.test") };
        services.AddSingleton<IDeliveryServiceClient>(new DeliveryServiceClient(http));
    }

    private static void ReplaceStoreWithThrowingOfflineMirror(IServiceCollection services)
    {
        services.RemoveAll<IAvailabilityStore>();
        services.AddSingleton<IAvailabilityStore>(new ThrowingOfflineMirrorStore());
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }

    /// <summary>
    /// An <see cref="IAvailabilityStore"/> whose offline mirror (<c>GoOfflineAsync</c>)
    /// always throws — simulating the unguarded withdraw-offer fan-out failure that
    /// pre-fix turned a successful offline toggle into a 500 (N13). Other methods
    /// are no-ops so the online/GET mirrors don't interfere with the offline test.
    /// </summary>
    private sealed class ThrowingOfflineMirrorStore : IAvailabilityStore
    {
        public Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
            => Task.FromResult(new JeeberAvailability { UserId = userId });

        public Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
            => Task.FromResult(new GoOnlineResult
            {
                Availability = new JeeberAvailability { UserId = userId },
                WasAlreadyOnline = false
            });

        public Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct)
            => throw new InvalidOperationException("simulated withdraw-offer fan-out failure (N13)");

        public Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberAvailability>>(Array.Empty<JeeberAvailability>());
    }
}
