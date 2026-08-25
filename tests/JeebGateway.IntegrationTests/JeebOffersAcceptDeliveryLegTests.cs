using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
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
/// S07 N7 / BR-10 — the ACTIVE V1 accept route (<c>POST /v1/offers/{id}/accept</c>,
/// <see cref="JeebGateway.Controllers.V1.JeebOffersController"/>) is the path the
/// mobile app actually calls. When <c>FeatureFlags:UseUpstream:Offer = true</c> and
/// the offer-service accept saga commits, the gateway BFF must assign the winning
/// jeeber onto the durable delivery row (the "DELIVERED leg") so the accepted
/// delivery counts against the jeeber's active-delivery cap. Previously only the
/// legacy (Obsolete) <c>/offers/{id}/accept</c> route did this, so the live mobile
/// path silently skipped the cap-sync.
///
/// offer-service and delivery-service are replaced by deterministic fakes; the
/// request row (carrying tier + pickup) is seeded via the real
/// <see cref="IRequestsStore"/> and the offerId→requestId pairing via the real
/// <see cref="IOfferRequestIndex"/>, exactly as a real submit records them.
/// </summary>
public class JeebOffersAcceptDeliveryLegTests
{
    private const string UpstreamFlashTierId = "1a2b3c4d-5e6f-5a1b-8c2d-3e4f5a6b7c8d";
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;

    [Fact]
    public async Task Accept_OnSagaSuccess_AssignsWinningJeeberOntoDeliveryRow()
    {
        var offerFake = AcceptedFake("offer-leg", "2f6d8b31-47ac-4e59-90b7-8d1c5a03e274");
        var deliveryFake = new RecordingDeliveryClient();
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-leg", requestId, "2f6d8b31-47ac-4e59-90b7-8d1c5a03e274");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-leg/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The DELIVERED leg was synced: a create-row call carried the winning jeeber,
        // the SAME row id (deliveryId == requestId), and the request's tier + pickup.
        var assignment = deliveryFake.Calls.SingleOrDefault(c => c.JeeberId == "2f6d8b31-47ac-4e59-90b7-8d1c5a03e274");
        assignment.Should().NotBeNull("the accepted delivery must be assigned to the winning jeeber");
        assignment!.Id.Should().Be(requestId);
        assignment.ClientId.Should().Be("client-owner");
        assignment.TierId.Should().Be(UpstreamFlashTierId,
            "post-accept upserts must repair legacy tier aliases to the upstream id");
        assignment.PickupLat.Should().Be(PickupLat);
        assignment.PickupLng.Should().Be(PickupLng);
    }

    [Fact]
    public async Task Accept_WhenDeliveryServiceFaults_StaysHttp200_DegradeDoNotFail()
    {
        var offerFake = AcceptedFake("offer-blip", "2f6d8b31-47ac-4e59-90b7-8d1c5a03e274");
        // Faults ONLY on the post-accept assignment (JeeberId set); create-time seed
        // (JeeberId null) succeeds so the request row is established normally.
        var deliveryFake = new RecordingDeliveryClient { ThrowOnJeeberAssignment = true };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-blip", requestId, "2f6d8b31-47ac-4e59-90b7-8d1c5a03e274");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-blip/accept", content: null);

        // A delivery-service blip must NEVER convert a committed accept into a 5xx.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.JeeberAssignmentAttempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Accept_WhenEnvelopeOmitsJeeber_Returns403_AndNeverAssigns()
    {
        // c2-1 (OD-C2-2): a winner unresolvable anywhere — no envelope id AND no index row —
        // cannot have its balance checked, so the accept is DENIED and nothing is assigned.
        // (The envelope-omits-BUT-index-has-it case — where the P0 fix resolves the winner
        // from the index and DOES assign — is covered in S03JeeberDeliveryListUpstreamAcceptTests.)
        var offerFake = AcceptedFake("offer-nojeeber", winningJeeberId: null);
        var deliveryFake = new RecordingDeliveryClient();
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        // 2-arg Record: no jeeber recorded in the index, so the P0 index fallback finds none.
        factory.Services.GetRequiredService<IOfferRequestIndex>().Record("offer-nojeeber", requestId);

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-nojeeber/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        deliveryFake.Calls.Should().NotContain(c => c.JeeberId != null);
    }

    // Retired BR-10 active-delivery cap — the /v1 accept route must not pre-count
    // delivery-service active rows before forwarding the accept saga.

    [Fact]
    public async Task Accept_WhenWinningJeeberHasTwoActiveDeliveries_ProceedsHttp200()
    {
        var offerFake = AcceptedFake("offer-cap", "6b0e35c9-1d84-4a72-bf13-9e57c2a806d1");
        var deliveryFake = new RecordingDeliveryClient { ActiveDeliveryCount = 2 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-cap", requestId, "6b0e35c9-1d84-4a72-bf13-9e57c2a806d1");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-cap/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.LastCountedJeeberId.Should().BeNull();
        offerFake.AcceptCallCount.Should().Be(1);
        deliveryFake.Calls.Should().Contain(c => c.JeeberId == "6b0e35c9-1d84-4a72-bf13-9e57c2a806d1");
    }

    [Fact]
    public async Task Accept_WhenWinningJeeberHasOneActiveDelivery_ProceedsHttp200()
    {
        var offerFake = AcceptedFake("offer-under", "8c73d21f-905b-4e60-a4d8-31f7b6c50e92");
        var deliveryFake = new RecordingDeliveryClient { ActiveDeliveryCount = 1 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-under", requestId, "8c73d21f-905b-4e60-a4d8-31f7b6c50e92");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-under/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.LastCountedJeeberId.Should().BeNull();
        offerFake.AcceptCallCount.Should().Be(1);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static FakeAcceptOfferClient AcceptedFake(string offerId, string? winningJeeberId)
        => new()
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = offerId,
                    JeeberId = winningJeeberId,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            },
        };

    private static WebApplicationFactory<Program> NewFactory(
        IOfferServiceClient fakeOffer, IDeliveryServiceClient fakeDelivery)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        { "FeatureFlags:UseUpstream:Delivery", "true" },
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fakeOffer);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(fakeDelivery);
                });
            });

    private static async Task<string> SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            TierId = "flash",
            PickupLocation = new GeoPoint { Lat = PickupLat, Lng = PickupLng },
            DropoffLocation = new GeoPoint { Lat = PickupLat + 0.01, Lng = PickupLng + 0.01 },
        }, CancellationToken.None);
        return created.Id;
    }

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    /// <summary>
    /// Test double for offer-service exercising only the accept-with-status seam used
    /// by the V1 accept route. Every other member throws — this route must not call them.
    /// </summary>
    private sealed class FakeAcceptOfferClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }
        public int AcceptCallCount { get; private set; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            AcceptCallCount++;
            return Task.FromResult(Result);
        }

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId,
            long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Records every <see cref="IDeliveryServiceClient.CreateDeliveryRowAsync"/> call so
    /// the suite can assert the post-accept winning-jeeber assignment. Optionally faults
    /// ONLY on the post-accept call (JeeberId set) to exercise the degrade-don't-fail
    /// contract while leaving the create-time seed (JeeberId null) intact. Every other
    /// member throws — the V1 accept path must not call them.
    /// </summary>
    private sealed class RecordingDeliveryClient : IDeliveryServiceClient
    {
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

        public ConcurrentQueue<CreateDeliveryRowUpstream> Calls { get; } = new();
        public bool ThrowOnJeeberAssignment { get; init; }
        public int JeeberAssignmentAttempts { get; private set; }

        // F2 / BR-10: when set, the pre-forward active-delivery count returns this value.
        // Null (default) preserves the "count not exercised" throw for the degrade tests.
        public int? ActiveDeliveryCount { get; init; }
        public string? LastCountedJeeberId { get; private set; }

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            Calls.Enqueue(body);
            if (!string.IsNullOrWhiteSpace(body.JeeberId))
            {
                JeeberAssignmentAttempts++;
                if (ThrowOnJeeberAssignment)
                    throw new DeliveryCreateRowException(503, "delivery-service unavailable");
            }
            return Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });
        }

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DeliveryTierDto>>(new[]
            {
                new DeliveryTierDto
                {
                    Id = UpstreamFlashTierId,
                    Name = "Flash",
                    SlaHours = 1,
                    RadiusKm = 8,
                    CommissionRate = 0.10,
                    PriceHint = "Fastest dispatch",
                    CreatedAt = DateTimeOffset.UnixEpoch,
                    UpdatedAt = DateTimeOffset.UnixEpoch,
                },
            });
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(
            string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
        {
            LastCountedJeeberId = jeeberId;
            if (ActiveDeliveryCount is int c) return Task.FromResult(c);
            throw new NotSupportedException();
        }
    }
}
