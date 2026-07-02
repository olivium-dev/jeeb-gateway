using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// PR-G3 (S09) — the caller-scoped delivery READ surfaces:
/// <list type="bullet">
///   <item><b>GET /deliveries</b> (ListShipments) — the upstream shipments feed is NOT
///     caller-scoped, so the gateway intersects it with the caller's OWN order ids
///     (request store) and drops foreign rows.</item>
///   <item><b>GET /deliveries/{id}</b> (GetById) — additive Amount (accepted-offer fee)
///     + JeeberName enrichment, ignore-when-null.</item>
/// </list>
/// </summary>
public class S09DeliveryReadScopingTests
{
    // -------- PR-G3a: shipments scoping ---------------------------------------

    [Fact]
    public async Task ListShipments_DropsForeignOrderIds_KeepsOnlyCallerOwned()
    {
        var fake = new ShipmentsFakeDeliveryClient();

        // GET /deliveries carries an explicit [Authorize] → the bearer-only DefaultPolicy,
        // so a real session bearer is required (the edge X-User-Id header does not satisfy it).
        using var factory = FactoryWith(fake);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613009001");

        // The caller owns exactly one order (its request id == order id).
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var owned = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "my parcel",
        }, CancellationToken.None);

        // Upstream returns a shipment for the caller's owned order AND a foreign order.
        fake.Shipments = new List<ShipmentDetailDto>
        {
            new() { Id = "shp-owned", OrderId = owned.Id, CurrentStage = "created" },
            new() { Id = "shp-foreign", OrderId = "order-not-mine", CurrentStage = "created" },
            new() { Id = "shp-nullorder", OrderId = null, CurrentStage = "created" },
        };

        var msg = new HttpRequestMessage(HttpMethod.Get, "/deliveries");
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var resp = await http.SendAsync(msg);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<ShipmentsEnvelope>();
        body!.Count.Should().Be(1, "only the caller-owned shipment survives the scoping intersection");
        body.Shipments.Should().ContainSingle(s => s.Id == "shp-owned");
        body.Shipments.Should().NotContain(s => s.Id == "shp-foreign");
        body.Shipments.Should().NotContain(s => s.Id == "shp-nullorder",
            "a shipment with no orderId cannot prove ownership and is dropped (fail closed)");
    }

    // -------- PR-G2: cancel propagates terminal to the canonical row -----------

    [Fact]
    public async Task Cancel_FlagOn_PropagatesCancelledToCanonicalRow_BestEffort()
    {
        var fake = new ShipmentsFakeDeliveryClient();
        using var factory = FactoryWith(fake, deliveryUpstream: true);

        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "cancel me",
        }, CancellationToken.None);
        await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        // The cancel endpoint is capability-gated (not [Authorize]), so the edge header path works.
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → jeeber

        var resp = await http.PostAsJsonAsync($"/deliveries/{created.Id}/cancel", new { reason = "bike broke" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Best-effort upstream propagation drove the canonical row terminal (Cancelled).
        fake.CanonicalTransitionCalls.Should().ContainSingle();
        fake.CanonicalTransitionCalls[0].DeliveryId.Should().Be(created.Id);
        fake.CanonicalTransitionCalls[0].To.Should().Be(CanonicalDeliveryStatus.Cancelled);
        fake.CanonicalTransitionCalls[0].ActorId.Should().Be(jeeberId);
        fake.CanonicalTransitionCalls[0].ActorRole.Should().Be("jeeber");
    }

    [Fact]
    public async Task Cancel_FlagOff_DoesNotPropagateUpstream()
    {
        var fake = new ShipmentsFakeDeliveryClient();
        using var factory = FactoryWith(fake, deliveryUpstream: false);

        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "cancel me",
        }, CancellationToken.None);
        await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await http.PostAsJsonAsync($"/deliveries/{created.Id}/cancel", new { reason = "bike broke" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        fake.CanonicalTransitionCalls.Should().BeEmpty(
            "with the delivery kill-switch off the gateway is authoritative — no canonical row to reconcile");
    }

    // -------- PR-G3b: GetById carries Amount = accepted fee --------------------

    [Fact]
    public async Task GetById_CarriesAmount_FromAcceptedOfferFee_And_JeeberName()
    {
        using var factory = new WebApplicationFactory<Program>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        const decimal acceptedFee = 27.50m;

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var offers = factory.Services.GetRequiredService<IPendingOffersStore>();
        var users = factory.Services.GetRequiredService<IUsersStore>();

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "deliver the box",
        }, CancellationToken.None);
        await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        // The accepted offer carries the agreed fee (deliveryId == requestId).
        var offer = await offers.TrySubmitAsync(
            created.Id, jeeberId, fee: acceptedFee, etaMinutes: 25, note: "on my way",
            maxPerRequest: 20, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);
        (await offers.AcceptAsync(offer.Id, DateTimeOffset.UtcNow, CancellationToken.None))
            .Should().BeTrue();

        // A cheap in-process display-name seam (gateway users projection).
        await users.UpsertProjectionAsync(new UserProfile
        {
            Id = jeeberId,
            Phone = "+9613000000",
            Name = "Karim Jeeber",
        }, CancellationToken.None);

        var http = AuthClient(factory, clientId);
        var resp = await http.GetAsync($"/deliveries/{created.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var dto = await resp.Content.ReadFromJsonAsync<DeliveryReadDto>();
        dto!.Amount.Should().Be(acceptedFee, "GetById surfaces the accepted offer's fee as Amount");
        dto.JeeberName.Should().Be("Karim Jeeber", "the jeeber display name resolves via the cheap users seam");
    }

    [Fact]
    public async Task GetById_WithoutAcceptedOffer_OmitsAmount()
    {
        // Additive + ignore-when-null: no accepted offer ⇒ Amount is absent from the JSON.
        using var factory = new WebApplicationFactory<Program>();
        var clientId = $"client-{Guid.NewGuid()}";

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "no offer yet",
        }, CancellationToken.None);

        var http = AuthClient(factory, clientId);
        var resp = await http.GetAsync($"/deliveries/{created.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotContain("\"amount\"", "Amount must be omitted (ignore-when-null) when no offer was accepted");
    }

    // ----------------------- helpers ------------------------------------------

    private const string AppId = "jeeb-test-app";

    private static WebApplicationFactory<Program> FactoryWith(
        IDeliveryServiceClient deliveryClient, bool deliveryUpstream = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", deliveryUpstream ? "true" : "false");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton(deliveryClient);

                // Stub the OTP upstream so MintSession can verify a real session bearer
                // (sub == userId), which the [Authorize] GET /deliveries route requires.
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtpClient());

                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.Delivery = deliveryUpstream;
                });
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    /// <summary>Mints a real session via the OTP verify path; returns (accessToken, userId == sub).</summary>
    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        var token = json.GetProperty("accessToken").GetString()!;
        var userId = json.GetProperty("user").GetProperty("userId").GetString()!;
        return (token, userId);
    }

    private sealed class StubOtpClient : IServiceOTPClient
    {
        public Task SendOTPAsync(SendOTPRequestUserID? body) => Task.CompletedTask;
        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body) => Task.CompletedTask;
        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    private sealed class ShipmentsEnvelope
    {
        [JsonPropertyName("shipments")] public List<ShipmentItem> Shipments { get; set; } = new();
        [JsonPropertyName("count")] public int Count { get; set; }
    }

    private sealed class ShipmentItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
    }

    private sealed class DeliveryReadDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("jeeberName")] public string? JeeberName { get; set; }
    }

    /// <summary>
    /// Fake <see cref="IDeliveryServiceClient"/> that returns a configurable shipments
    /// list and throws for every other method (none are exercised by these tests).
    /// </summary>
    private sealed class ShipmentsFakeDeliveryClient : IDeliveryServiceClient
    {
        public List<ShipmentDetailDto> Shipments { get; set; } = new();

        // PR-G2: records the best-effort cancellation propagation to the canonical row.
        public List<(string DeliveryId, string To, string PartySource, string ActorId, string ActorRole)> CanonicalTransitionCalls { get; } = new();

        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => Task.FromResult(new ShipmentsListDto { Shipments = Shipments, Count = Shipments.Count });

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
        {
            CanonicalTransitionCalls.Add((deliveryId, to, partySource, actorId, actorRole));
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = to,
                TransitionId = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow,
            });
        }

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
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
