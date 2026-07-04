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
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;

    [Fact]
    public async Task Accept_OnSagaSuccess_AssignsWinningJeeberOntoDeliveryRow()
    {
        var offerFake = AcceptedFake("offer-leg", "jeeber-win");
        var deliveryFake = new RecordingDeliveryClient();
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-leg", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-leg/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // The DELIVERED leg was synced: a create-row call carried the winning jeeber,
        // the SAME row id (deliveryId == requestId), and the request's tier + pickup.
        var assignment = deliveryFake.Calls.SingleOrDefault(c => c.JeeberId == "jeeber-win");
        assignment.Should().NotBeNull("the accepted delivery must be assigned to the winning jeeber");
        assignment!.Id.Should().Be(requestId);
        assignment.ClientId.Should().Be("client-owner");
        assignment.TierId.Should().Be("flash");
        assignment.PickupLat.Should().Be(PickupLat);
        assignment.PickupLng.Should().Be(PickupLng);
    }

    [Fact]
    public async Task Accept_WhenDeliveryServiceFaults_StaysHttp200_DegradeDoNotFail()
    {
        var offerFake = AcceptedFake("offer-blip", "jeeber-win");
        // Faults ONLY on the post-accept assignment (JeeberId set); create-time seed
        // (JeeberId null) succeeds so the request row is established normally.
        var deliveryFake = new RecordingDeliveryClient { ThrowOnJeeberAssignment = true };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-blip", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-blip/accept", content: null);

        // A delivery-service blip must NEVER convert a committed accept into a 5xx.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.JeeberAssignmentAttempts.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Accept_WhenEnvelopeOmitsJeeber_DoesNotAttemptAssignment_StaysHttp200()
    {
        // Saga committed but the winner is unresolvable anywhere — the envelope carried no
        // jeeber id AND the routing index recorded none (2-arg submit) — so the gateway must
        // never write a blank jeeber onto the delivery row; the accept still succeeds.
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

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.Calls.Should().NotContain(c => c.JeeberId != null);
    }

    // F2 / BR-10 (gateway-flow-correctness-audit) — the /v1 accept route must enforce
    // the per-jeeber active-delivery cap BEFORE forwarding the accept saga.

    [Fact]
    public async Task Accept_WhenWinningJeeberAtCap_Returns409_AndDoesNotForwardSaga()
    {
        var offerFake = AcceptedFake("offer-cap", "jeeber-busy");
        var deliveryFake = new RecordingDeliveryClient { ActiveDeliveryCount = 2 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-cap", requestId, "jeeber-busy");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-cap/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/too-many-active-deliveries");

        deliveryFake.LastCountedJeeberId.Should().Be("jeeber-busy");
        // The cap short-circuits BEFORE the accept saga — no third delivery is created.
        offerFake.AcceptCallCount.Should().Be(0);
        deliveryFake.Calls.Should().NotContain(c => c.JeeberId != null);
    }

    [Fact]
    public async Task Accept_WhenWinningJeeberUnderCap_ProceedsHttp200()
    {
        var offerFake = AcceptedFake("offer-under", "jeeber-ok");
        var deliveryFake = new RecordingDeliveryClient { ActiveDeliveryCount = 1 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-under", requestId, "jeeber-ok");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-under/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.LastCountedJeeberId.Should().Be("jeeber-ok");
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
            => throw new NotSupportedException();
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
