using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P0 fix-iteration — END-TO-END reproduction through the LIVE upstream accept route.
///
/// <para>The original P0 read fix (<see cref="IRequestsStore.ListForJeeberAsync"/>) was
/// correct but unproven on the live path: the only local <c>JeeberId</c> WRITE lived on the
/// legacy in-memory accept path (<see cref="IRequestsStore.TryAcceptByJeeberAsync"/>), which is
/// NOT exercised when <c>FeatureFlags:UseUpstream:Offer = true</c> (the deployed posture). On
/// the upstream path the gateway projected only the STATUS and never stamped the assignee, so
/// the jeeber's <c>GET /v1/deliveries</c> / <c>GET /v1/requests?role=jeeber</c> came back empty.
/// The earlier test seeded via TryAcceptByJeeberAsync and so masked the gap.</para>
///
/// <para>These tests drive the REAL route <c>POST /v1/offers/{id}/accept</c> with the Offer
/// upstream flag ON (offer-service replaced by a fake), then assert the jeeber sees the
/// delivery. The decisive case is <see cref="Accept_WhenEnvelopeOmitsJeeber_StampsFromIndex_JeeberSeesDelivery"/>:
/// the accept envelope carries NO jeeber id (the observed live behavior), so the winner must be
/// resolved from the offer routing index recorded at submit and stamped onto the local row.</para>
/// </summary>
public class S03JeeberDeliveryListUpstreamAcceptTests
{
    private const string ClientOwner = "client-nour";
    private const string Winner = "jeeber-karim";
    private const double Lat = 33.5138;
    private const double Lng = 36.2765;

    [Fact]
    public async Task Accept_WhenEnvelopeOmitsJeeber_StampsFromIndex_JeeberSeesDelivery()
    {
        // LIVE scenario: offer-service accept response omits actor_id/jeeber_id, so the
        // envelope JeeberId is null and the winner must come from the routing index.
        using var factory = NewFactory(envelopeJeeberId: null);
        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-x1", requestId, Winner); // index records the bidder at submit

        var accept = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-x1/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var deliveries = await JeeberActor(factory, Winner)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");
        deliveries!.Items.Should().ContainSingle(i => i.Id == requestId,
            "after a live upstream accept the assigned jeeber must see the delivery even when "
            + "the accept envelope omitted the jeeber id");
        deliveries.Items.Single(i => i.Id == requestId).JeeberId.Should().Be(Winner);

        var jobs = await JeeberActor(factory, Winner)
            .GetFromJsonAsync<PagedEnvelope>("/v1/requests?role=jeeber");
        jobs!.Items.Should().ContainSingle(i => i.Id == requestId);
    }

    [Fact]
    public async Task Accept_WhenEnvelopeHasJeeber_JeeberSeesDelivery()
    {
        // Envelope carries the winner → used directly (index need not have it).
        using var factory = NewFactory(envelopeJeeberId: Winner);
        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-x2", requestId); // 2-arg: no jeeber in the index

        var accept = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-x2/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var deliveries = await JeeberActor(factory, Winner)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");
        deliveries!.Items.Should().ContainSingle(i => i.Id == requestId);
        deliveries.Items.Single(i => i.Id == requestId).JeeberId.Should().Be(Winner);
    }

    [Fact]
    public async Task Accept_WhenNeitherEnvelopeNorIndexHasJeeber_ListStaysEmpty_AcceptStill200()
    {
        // Degrade: no winner resolvable anywhere → the local row's JeeberId is never blanked
        // (no-op write), the jeeber list is empty, and the committed accept still returns 200.
        using var factory = NewFactory(envelopeJeeberId: null);
        var requestId = await SeedRequestAsync(factory, ClientOwner);
        SeedRouting(factory, "offer-x3", requestId); // 2-arg: no jeeber recorded

        var accept = await ClientActor(factory, ClientOwner)
            .PostAsync("/v1/offers/offer-x3/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var deliveries = await JeeberActor(factory, Winner)
            .GetFromJsonAsync<PagedEnvelope>("/v1/deliveries");
        deliveries!.Items.Should().NotContain(i => i.Id == requestId);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(string? envelopeJeeberId)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        { "FeatureFlags:UseUpstream:Chat", "false" },
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(new FakeAcceptOfferClient(envelopeJeeberId));
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton<IDeliveryServiceClient>(new NoopDeliveryClient());
                });
            });

    private static async Task<string> SeedRequestAsync(WebApplicationFactory<Program> factory, string clientId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Deliver the parcel",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = Lat, Lng = Lng },
            DropoffLocation = new GeoPoint { Lat = Lat + 0.01, Lng = Lng + 0.01 },
        }, CancellationToken.None);
        return created.Id;
    }

    private static void SeedRouting(WebApplicationFactory<Program> factory, string offerId, string requestId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId);

    private static void SeedRouting(WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    private static HttpClient JeeberActor(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver"); // → contract jeeber
        return c;
    }

    private sealed class PagedEnvelope
    {
        [JsonPropertyName("items")] public List<Item> Items { get; set; } = new();
    }

    private sealed class Item
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("jeeberId")] public string? JeeberId { get; set; }
    }

    private sealed class FakeAcceptOfferClient : IOfferServiceClient
    {
        private readonly OfferAcceptResult _result;
        public FakeAcceptOfferClient(string? envelopeJeeberId)
            => _result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer",
                    JeeberId = envelopeJeeberId, // null reproduces the observed live envelope
                    RejectedOfferIds = Array.Empty<string>(),
                },
            };

        public Task<OfferAcceptResult> AcceptWithStatusAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(_result);
        public Task<OfferAcceptWire> AcceptAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class NoopDeliveryClient : IDeliveryServiceClient
    {
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult(0);
    }
}
