using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Matching;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S06 (B1/B2/B3/ALT-2/ALT-3/ALT-4/ALT-4b/N5/N6): the just-in-time delivery-row
/// mirror on <c>POST /matching/run</c>.
///
/// ROOT CAUSE these tests lock: a request created at the gateway lives only in the
/// in-memory <see cref="IRequestsStore"/>; delivery-service (which owns the
/// matching domain + the <c>deliveries</c> table) has no row, so request_id-mode
/// <c>POST /api/v1/matching/run</c> returns <c>404 unknown_request_id</c>. The
/// gateway now best-effort seeds the canonical row (idempotent
/// <c>POST /api/v1/deliveries</c>) immediately before forwarding the run.
///
/// These assert the BFF orchestration contract, not delivery-service internals:
///  - happy path: request_id mode seeds the row THEN runs matching (seed hits
///    /api/v1/deliveries with the SAME id + snake_case columns; run hits
///    /api/v1/matching/run; the gateway maps the 200 result);
///  - negative path: the dry-run/preview shape (no requestId) NEVER seeds — only
///    the run is forwarded — and a request_id the gateway does not know is NOT
///    seeded, so delivery-service's canonical 404 surfaces as ProblemDetails.
/// </summary>
public class MatchingRunMirrorTests
{
    private const double PickupLat = 24.6309;
    private const double PickupLng = 46.7194;

    [Fact]
    public async Task RequestId_Mode_Seeds_Delivery_Row_Before_Running_Matching()
    {
        // The gateway holds the request only in its in-memory store. The mirror
        // must seed the canonical delivery row, then run matching — both against
        // delivery-service, seed first.
        var paths = new List<string>();
        var deliveriesBody = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            paths.Add(path);

            if (path == "/api/v1/deliveries")
            {
                deliveriesBody.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                // delivery-service echoes the row id as delivery_id (snake_case).
                return JsonResponse("""{"delivery_id":"req-seed-1","tenant_id":"default","status":"Ordered"}""",
                    HttpStatusCode.Created);
            }

            // /api/v1/matching/run — the resolve now succeeds because the row exists.
            return JsonResponse(
                """
                {"request_id":"req-seed-1","tier_id":"uuid-x","tier_code":"flash","radius_km":5,
                 "notified_count":3,"candidate_count":3,
                 "candidates":[{"user_id":"u1","vehicle_type":"car","distance_km":1.1,"rating":4.9}],
                 "elapsed_ms":12}
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        // Seed the gateway in-memory request row (what create would have produced).
        await SeedRequestAsync(factory, id: "req-seed-1", clientId: "client-1", tierId: "flash");

        var client = ClientFor(factory, "client-1");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "req-seed-1" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.RequestId.Should().Be("req-seed-1");
        body.TierId.Should().Be("flash");
        body.CandidateCount.Should().Be(3);

        // Seed ran BEFORE the run, and exactly once.
        paths.Should().ContainInOrder("/api/v1/deliveries", "/api/v1/matching/run");
        paths.Count(p => p == "/api/v1/deliveries").Should().Be(1);

        // The seed carried the SAME id and snake_case matching-resolve columns.
        var seed = deliveriesBody.Single();
        seed.Should().Contain("\"id\":\"req-seed-1\"");
        seed.Should().Contain("\"client_id\":\"client-1\"");
        seed.Should().Contain("\"tier_id\"");
        seed.Should().Contain("\"pickup_lat\"");
        seed.Should().Contain("\"pickup_lng\"");
        seed.Should().Contain("\"tenant_id\":\"default\"");
    }

    [Fact]
    public async Task Seed_409_Conflict_Is_Idempotent_And_Run_Still_Proceeds()
    {
        // A pre-existing row (create-time mirror already seeded it, or a retried
        // run) returns 409 from delivery-service. The typed client treats that as
        // idempotent success, so the run must still proceed and return 200.
        var paths = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            paths.Add(path);

            if (path == "/api/v1/deliveries")
            {
                return JsonResponse("""{"reason":"already_exists"}""", HttpStatusCode.Conflict);
            }

            return JsonResponse(
                """
                {"request_id":"req-dupe","tier_id":"uuid-y","tier_code":"standard","radius_km":2,
                 "notified_count":1,"candidate_count":1,"candidates":[],"elapsed_ms":4}
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        await SeedRequestAsync(factory, id: "req-dupe", clientId: "client-2", tierId: "standard");

        var client = ClientFor(factory, "client-2");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "req-dupe" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.TierId.Should().Be("standard");
        paths.Should().ContainInOrder("/api/v1/deliveries", "/api/v1/matching/run");
    }

    [Fact]
    public async Task DryRun_Preview_Shape_Does_Not_Seed_A_Row()
    {
        // The pickup/tier dry-run shape carries no requestId — there is nothing to
        // seed and no row to persist. The mirror must be a no-op: only the run is
        // forwarded.
        var paths = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return JsonResponse(
                """
                {"request_id":"","tier_id":"same-day","radius_km":15,
                 "notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":3}
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-dry");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "same-day"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // No seed: /api/v1/deliveries was never called.
        paths.Should().NotContain("/api/v1/deliveries");
        paths.Should().ContainSingle().Which.Should().Be("/api/v1/matching/run");
    }

    [Fact]
    public async Task Unknown_RequestId_Is_Not_Seeded_And_Upstream_404_Surfaces()
    {
        // A request_id the gateway does not know (never created here) has no local
        // row to mirror, so nothing is seeded. delivery-service stays the
        // canonical authority and its 404 surfaces as RFC 7807 ProblemDetails.
        var paths = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            paths.Add(path);
            // delivery-service rejects the unknown request id.
            return JsonResponse("""{"reason":"unknown_request_id"}""", HttpStatusCode.NotFound);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-unknown");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "does-not-exist" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(404);
        problem.GetProperty("detail").GetString().Should().Be("unknown_request_id");

        // No seed was attempted for an unknown id.
        paths.Should().NotContain("/api/v1/deliveries");
        paths.Should().ContainSingle().Which.Should().Be("/api/v1/matching/run");
    }

    [Fact]
    public async Task Seed_Failure_Is_Swallowed_And_Run_Still_Forwards()
    {
        // BEST-EFFORT: a non-idempotent seed error (e.g. 500) must NOT fail the
        // matching run. The run still forwards and delivery-service owns the
        // outcome.
        var paths = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            paths.Add(path);

            if (path == "/api/v1/deliveries")
            {
                return JsonResponse("""{"reason":"boom"}""", HttpStatusCode.InternalServerError);
            }

            return JsonResponse(
                """
                {"request_id":"req-soft","tier_id":"uuid-z","tier_code":"express","radius_km":1,
                 "notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":2}
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        await SeedRequestAsync(factory, id: "req-soft", clientId: "client-soft", tierId: "express");

        var client = ClientFor(factory, "client-soft");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "req-soft" });

        // The seed 500 was swallowed; the run still ran and returned 200.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.TierId.Should().Be("express");
        paths.Should().ContainInOrder("/api/v1/deliveries", "/api/v1/matching/run");
    }

    [Fact]
    public async Task Mirror_Disabled_Flag_Skips_The_Seed()
    {
        // Instant rollback lever: FeatureFlags:MatchingMirror:Enabled=false makes
        // the controller forward-only (no pre-run seed), exactly the pre-S06 path.
        var paths = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            paths.Add(req.RequestUri!.AbsolutePath);
            return JsonResponse(
                """
                {"request_id":"req-off","tier_id":"uuid-off","tier_code":"flash","radius_km":1,
                 "notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":1}
                """);
        });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:MatchingMirror:Enabled", "false");
            builder.ConfigureTestServices(services =>
                ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));
        });

        await SeedRequestAsync(factory, id: "req-off", clientId: "client-off", tierId: "flash");

        var client = ClientFor(factory, "client-off");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "req-off" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // Flag off → no seed.
        paths.Should().NotContain("/api/v1/deliveries");
        paths.Should().ContainSingle().Which.Should().Be("/api/v1/matching/run");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(Action<IServiceCollection>? configureServices = null)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            if (configureServices is not null)
            {
                builder.ConfigureTestServices(configureServices);
            }
        });
    }

    private static async Task SeedRequestAsync(
        WebApplicationFactory<Program> factory, string id, string clientId, string tierId)
    {
        // Seed the gateway's in-memory request row exactly as a create would —
        // this is the row that today lives ONLY at the gateway (the S06 gap).
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        await store.CreateAsync(new CreateRequestInput
        {
            Id = id,
            ClientId = clientId,
            Description = "seeded for matching mirror test",
            TierId = tierId,
            PickupLocation = new GeoPoint { Lat = PickupLat, Lng = PickupLng },
            DropoffLocation = new GeoPoint { Lat = PickupLat + 0.01, Lng = PickupLng + 0.01 },
        }, CancellationToken.None);
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static void ReplaceDeliveryClient(
        IServiceCollection services, HttpMessageHandler handler, string baseUrl)
    {
        services.RemoveAll<IDeliveryServiceClient>();
        var http = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
        services.AddSingleton<IDeliveryServiceClient>(new DeliveryServiceClient(http));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
