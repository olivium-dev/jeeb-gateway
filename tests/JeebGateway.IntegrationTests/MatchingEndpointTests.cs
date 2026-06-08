using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Matching;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-008 / DELIVERY-SERVICE-RELOCATION-DESIGN.md §2.1 + §5.
///
/// Courier matching was relocated out of the gateway into delivery-service. The
/// gateway no longer owns a matching engine; <c>POST /matching/run</c> is a thin
/// BFF that always delegates to delivery-service
/// <c>POST /api/v1/matching/run</c> via <see cref="IDeliveryServiceClient"/>.
///
/// These tests therefore exercise the DELEGATION contract — the request is
/// forwarded to the delivery upstream, the snake_case Go result is mapped onto
/// the gateway DTO, and the upstream's 400/404/422 are surfaced as
/// ProblemDetails — NOT the engine internals (radius math, Haversine ordering,
/// 10k-Jeeber perf), which are now delivery-service's responsibility and live in
/// that service's own test suite. The on-the-wire JSON binding of the literal Go
/// body is locked separately by <see cref="MatchingRunContractTests"/>.
/// </summary>
public class MatchingEndpointTests
{
    private const double PickupLat = 24.6309;
    private const double PickupLng = 46.7194;

    [Fact]
    public async Task Run_Forwards_To_Delivery_Upstream_And_Maps_Result()
    {
        // Happy path: the controller forwards to delivery-service /api/v1/matching/run
        // and maps the snake_case result onto the gateway MatchingRunResponse.
        var captured = new CapturedRequests();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            return JsonResponse(
                """
                {
                  "request_id":"del_123","tier_id":"urgent","radius_km":5,
                  "notified_count":4,"candidate_count":9,
                  "candidates":[{"user_id":"u1","vehicle_type":"car","distance_km":1.2,"rating":4.8}],
                  "elapsed_ms":38
                }
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-1");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            requestId = "del_123",
            allowedVehicleTypes = new[] { "car", "motorbike" }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.RequestId.Should().Be("del_123");
        body.TierId.Should().Be("urgent");
        body.RadiusKm.Should().Be(5.0);
        body.NotifiedCount.Should().Be(4);
        body.CandidateCount.Should().Be(9);
        body.ElapsedMs.Should().Be(38);
        body.Candidates.Should().ContainSingle()
            .Which.UserId.Should().Be("u1");
        body.Candidates[0].VehicleType.Should().Be("car");
        body.Candidates[0].DistanceKm.Should().Be(1.2);
        body.Candidates[0].Rating.Should().Be(4.8);

        // The request hit the canonical delivery-service route.
        captured.Single().RequestUri!.AbsolutePath.Should().Be("/api/v1/matching/run");
    }

    [Fact]
    public async Task Run_Surfaces_TierCode_As_TierId_For_S06_Contract()
    {
        // S06 step B1 asserts $.tierId == "flash" (the lowercase tier CODE, not the
        // UUID). delivery-service returns BOTH tier_code (flash/standard/express)
        // and tier_id (the stable UUID). The BFF must echo the code as $.tierId.
        var stub = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """
                {
                  "request_id":"flash_req","tier_id":"8f1c2d3e-0000-4000-8000-000000000001",
                  "tier_code":"flash","radius_km":1,
                  "notified_count":2,"candidate_count":2,
                  "candidates":[{"user_id":"kamal","vehicle_type":"motorbike","distance_km":0.3,"rating":4.9}],
                  "elapsed_ms":12
                }
                """));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-flash");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "flash_req" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        // The lowercase CODE is surfaced as $.tierId — NOT the UUID.
        body!.TierId.Should().Be("flash");
        body.Candidates.Should().ContainSingle().Which.UserId.Should().Be("kamal");
    }

    [Fact]
    public async Task Run_Falls_Back_To_TierId_Uuid_When_TierCode_Absent()
    {
        // Backward-compatibility: an older delivery-service that has not yet shipped
        // tier_code returns only tier_id. The BFF must then echo the UUID (today's
        // behaviour) rather than emit an empty $.tierId.
        var stub = new StubHttpMessageHandler(_ =>
            JsonResponse(
                """
                {"request_id":"r","tier_id":"urgent-uuid","radius_km":5,
                 "notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":1}
                """));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-legacy");
        var resp = await client.PostAsJsonAsync("/matching/run", new { requestId = "r" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MatchingRunResponse>();
        body!.TierId.Should().Be("urgent-uuid");
    }

    [Fact]
    public async Task Run_Sends_SnakeCase_Body_With_Tenant_To_Upstream()
    {
        // The gateway forwards the body verbatim AND stamps the required
        // tenant_id; the wire body delivery-service receives must be snake_case.
        var captured = new CapturedRequests();
        var captureBodies = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            captured.Add(req);
            captureBodies.Add(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse(
                """
                {"request_id":"del_9","tier_id":"same-day","radius_km":15,
                 "notified_count":0,"candidate_count":0,"candidates":[],"elapsed_ms":3}
                """);
        });

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-body");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "same-day"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var sent = captureBodies.Single();
        // snake_case (Go) field names on the wire — the recurring seam bug guard.
        sent.Should().Contain("\"pickup_lat\"");
        sent.Should().Contain("\"pickup_lng\"");
        sent.Should().Contain("\"tier_id\"");
        sent.Should().Contain("\"tenant_id\"");
        sent.Should().Contain("default"); // default tenant
        sent.Should().NotContain("pickupLat"); // no camelCase leakage
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "unknown vehicle")]
    [InlineData(HttpStatusCode.NotFound, "unknown tier")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "non-positive radius")]
    public async Task Run_Maps_Upstream_Error_To_ProblemDetails(HttpStatusCode upstreamStatus, string reason)
    {
        // delivery-service owns validation. Its 400/404/422 must surface straight
        // through as RFC 7807 with the same status code.
        var stub = new StubHttpMessageHandler(_ =>
            JsonResponse($$"""{"reason":"{{reason}}"}""", upstreamStatus));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-err");
        var resp = await client.PostAsJsonAsync("/matching/run", new
        {
            pickupLat = PickupLat,
            pickupLng = PickupLng,
            tierId = "x"
        });

        resp.StatusCode.Should().Be(upstreamStatus);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be((int)upstreamStatus);
        problem.GetProperty("detail").GetString().Should().Be(reason);
    }

    [Fact]
    public async Task Run_Returns_400_When_Body_Missing()
    {
        // Empty body is rejected at the gateway before any upstream call.
        var stub = new StubHttpMessageHandler(_ =>
            throw new InvalidOperationException("upstream must not be called for an empty body"));

        using var factory = NewFactory(services =>
            ReplaceDeliveryClient(services, stub, "http://upstream-delivery.test"));

        var client = ClientFor(factory, "client-empty");
        var resp = await client.PostAsync("/matching/run",
            new StringContent("null", Encoding.UTF8, "application/json"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Run_Requires_Identity()
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
    public async Task Run_Rejects_Caller_Without_Client_Role()
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

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

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

    private sealed class CapturedRequests
    {
        private readonly List<HttpRequestMessage> _items = new();
        public void Add(HttpRequestMessage req) => _items.Add(req);
        public HttpRequestMessage Single() => _items.Single();
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_handler(request));
    }
}
