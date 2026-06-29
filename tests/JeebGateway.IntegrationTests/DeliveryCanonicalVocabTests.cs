using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Canonical-vocab gateway fix (JEB-45). With
/// <c>FeatureFlags:UseUpstream:Delivery = true</c> the gateway must:
/// <list type="bullet">
///   <item>accept the canonical PATCH body the suite drives
///     (<c>{trigger:"pickup"}</c>, <c>{to:"Picked"}</c>, legacy <c>{status:"in_transit"}</c>)
///     and forward it to delivery-service's
///     <c>POST /api/v1/deliveries/{id}/transition</c>, returning the canonical
///     status (Ordered/Picked/InTransit/AtDoor/Done) verbatim — NOT the legacy
///     snake_case literal;</item>
///   <item>forward without any in-gateway legacy-enum guard (the linear
///     state machine was retired in JEB-1479);</item>
///   <item>surface a delivery-service 422 (illegal edge) as the gateway's 422 with
///     the typed from/to/trigger extension fields;</item>
///   <item>read-through GET /deliveries/{id} so <c>$.status</c> is canonical;</item>
///   <item>404 (not 500) for an unknown id on the canonical read.</item>
/// </list>
/// All assertions drive the REAL controller; the upstream delivery-service is a
/// recordable in-process fake so the gateway BFF wiring is what is under test.
/// </summary>
public class DeliveryCanonicalVocabTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DeliveryCanonicalVocabTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private WebApplicationFactory<Program> Factory(RecordingDeliveryClient client)
        => _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(client);
            });
        });

    private static HttpClient Driver(WebApplicationFactory<Program> factory, string userId, string role = "driver")
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    // ---------------- PATCH /deliveries/{id}/status (canonical forward) ----------

    [Theory]
    [InlineData("pickup", "Picked")]
    [InlineData("depart", "InTransit")]
    [InlineData("arrive", "AtDoor")]
    public async Task PatchStatus_FriendlyTrigger_ForwardsCanonicalTarget_Returns200CanonicalStatus(
        string triggerWord, string expectedCanonical)
    {
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-1");

        var resp = await http.PatchAsync(
            "/deliveries/del-1/status",
            JsonContent.Create(new { trigger = triggerWord }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<StatusDto>();
        dto!.Status.Should().Be(expectedCanonical, "the gateway surfaces the canonical status verbatim");

        // The legacy-enum ValidateTransition is bypassed: the call reached the
        // canonical upstream with the mapped target + jeeber party source.
        fake.TransitionCalls.Should().ContainSingle();
        fake.TransitionCalls[0].To.Should().Be(expectedCanonical);
        fake.TransitionCalls[0].PartySource.Should().Be("jeeber");
        fake.TransitionCalls[0].ActorId.Should().Be("jeeber-1");
    }

    [Fact]
    public async Task PatchStatus_ExplicitCanonicalTo_Forwards_Returns200()
    {
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-2");

        var resp = await http.PatchAsync(
            "/deliveries/del-2/status",
            JsonContent.Create(new { to = "Picked" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<StatusDto>())!.Status.Should().Be("Picked");
        fake.TransitionCalls[0].To.Should().Be("Picked");
    }

    [Fact]
    public async Task PatchStatus_LegacySnakeStatusAlias_MapsToCanonical_Returns200()
    {
        // S09 drives {status:"in_transit"} expecting canonical InTransit.
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-3");

        var resp = await http.PatchAsync(
            "/deliveries/del-3/status",
            JsonContent.Create(new { status = "in_transit" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<StatusDto>())!.Status.Should().Be("InTransit");
        fake.TransitionCalls[0].To.Should().Be("InTransit");
    }

    [Fact]
    public async Task PatchStatus_AdminResolve_ForwardsAdminPartySource()
    {
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);
        // The DeliveriesController class-level capability is {client, jeeber}
        // (delivery.participate). The S15 A3 admin-resolve actor carries a
        // participant role too; we model that with driver+admin so the request
        // passes the gate AND maps to the admin party source for the upstream.
        var http = Driver(factory, "admin-1", role: "driver,admin");

        var resp = await http.PatchAsync(
            "/deliveries/del-4/status",
            JsonContent.Create(new { trigger = "admin_resolve", to = "Done" }));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadFromJsonAsync<StatusDto>())!.Status.Should().Be("Done");
        fake.TransitionCalls[0].To.Should().Be("Done");
        fake.TransitionCalls[0].PartySource.Should().Be("admin");
    }

    [Fact]
    public async Task PatchStatus_IllegalTransition_Returns422_WithTypedBody()
    {
        // delivery-service rejects an illegal edge with a typed 422; the gateway
        // forwards it verbatim and does NOT re-validate against the legacy enum.
        var fake = new RecordingDeliveryClient
        {
            TransitionThrows = new DeliveryTransitionException(
                statusCode: (int)HttpStatusCode.UnprocessableEntity,
                reason: "transition_not_allowed",
                from: "Ordered",
                to: "AtDoor",
                trigger: "jeeber")
        };
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-5");

        var resp = await http.PatchAsync(
            "/deliveries/del-5/status",
            JsonContent.Create(new { to = "AtDoor" }));

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/transition-not-allowed");
        problem.Extensions.Should().ContainKey("from");
        problem.Extensions.Should().ContainKey("to");
        problem.Extensions.Should().ContainKey("trigger");
    }

    [Fact]
    public async Task PatchStatus_UnknownDelivery_Returns404()
    {
        var fake = new RecordingDeliveryClient
        {
            TransitionThrows = new DeliveryTransitionException((int)HttpStatusCode.NotFound, "not_found")
        };
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-6");

        var resp = await http.PatchAsync(
            "/deliveries/missing/status",
            JsonContent.Create(new { trigger = "pickup" }));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PatchStatus_NoRecognizableTarget_Returns400()
    {
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-7");

        var resp = await http.PatchAsync(
            "/deliveries/del-7/status",
            JsonContent.Create(new { nonsense = "x" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        fake.TransitionCalls.Should().BeEmpty("no upstream call when the target cannot be resolved");
    }

    [Fact]
    public async Task PatchStatus_WithoutAuth_Returns401()
    {
        var fake = new RecordingDeliveryClient();
        await using var factory = Factory(fake);

        var resp = await factory.CreateClient().PatchAsync(
            "/deliveries/del-8/status",
            JsonContent.Create(new { trigger = "pickup" }));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ---------------- GET /deliveries/{id} (canonical read-through) --------------

    [Fact]
    public async Task GetById_FlagOn_ReadsThroughCanonicalStatus()
    {
        var fake = new RecordingDeliveryClient
        {
            ReadReturns = new DeliveryReadUpstream
            {
                DeliveryId = "del-9",
                ClientId = "client-9",
                JeeberId = "jeeber-9",
                Status = "Ordered",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-9");

        var resp = await http.GetAsync("/deliveries/del-9");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await resp.Content.ReadFromJsonAsync<StatusDto>();
        dto!.Status.Should().Be("Ordered", "the canonical read-through surfaces the SM-1 vocab");
        fake.ReadCalls.Should().Contain("del-9");
    }

    [Fact]
    public async Task GetById_FlagOn_UnknownId_Returns404_NotServerError()
    {
        var fake = new RecordingDeliveryClient { ReadReturnsNull = true };
        await using var factory = Factory(fake);
        var http = Driver(factory, "jeeber-10");

        var resp = await http.GetAsync($"/deliveries/unknown-{Guid.NewGuid()}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ((int)resp.StatusCode).Should().BeLessThan(500);
    }

    // ---------------- DTOs / recordable fake -------------------------------------

    private sealed record StatusDto(string Id, string Status);

    private sealed class RecordingDeliveryClient : IDeliveryServiceClient
    {
        // S03: jeeber available-requests feed is not exercised by these tests.
        public Task<JeeberAvailableRequestsResult> GetAvailableRequestsAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult(new JeeberAvailableRequestsResult());

        public List<(string DeliveryId, string To, string PartySource, string ActorId, string ActorRole)> TransitionCalls { get; } = new();
        public List<string> ReadCalls { get; } = new();
        public DeliveryTransitionException? TransitionThrows { get; set; }
        public DeliveryReadUpstream? ReadReturns { get; set; }
        public bool ReadReturnsNull { get; set; }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            TransitionCalls.Add((deliveryId, to, partySource, actorId, actorRole));
            if (TransitionThrows is not null) throw TransitionThrows;
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = to,
                TransitionId = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow
            });
        }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
        {
            ReadCalls.Add(deliveryId);
            if (ReadReturnsNull) return Task.FromResult<DeliveryReadUpstream?>(null);
            return Task.FromResult<DeliveryReadUpstream?>(ReadReturns);
        }

        // Unused by these tests — fail loudly if hit.
        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
    }
}
