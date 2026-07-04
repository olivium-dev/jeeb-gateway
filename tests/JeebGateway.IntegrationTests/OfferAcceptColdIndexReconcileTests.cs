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
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// fix/offer-notpending-fullflow — the ACTIVE V1 accept route
/// (<c>POST /v1/offers/{id}/accept</c>, <see cref="JeebGateway.Controllers.V1.JeebOffersController"/>,
/// the path the mobile app actually calls with <c>UseUpstream:Offer=true</c>) must
/// resolve the offer's <c>offerId → (requestId, jeeberId)</c> pairing AUTHORITATIVELY
/// from offer-service, so a stale/empty in-process routing index (a gateway restart or
/// a replica that never saw the submit) can NEVER turn a genuinely-pending offer into a
/// false "this offer is no longer available".
///
/// <para>Before the fix, an index MISS returned a bare 404 that the client renders as
/// "offer no longer available" — a FALSE unavailability produced purely by lost gateway
/// memory. The fix reconciles the miss from the authoritative owner-scoped offer-service
/// list (<c>GET /api/v1/requests/{id}/offers</c>) and re-hydrates the index.</para>
///
/// offer-service and delivery-service are replaced by deterministic fakes; the request
/// row is seeded via the real <see cref="IRequestsStore"/>. Crucially, these tests do
/// NOT seed the <see cref="IOfferRequestIndex"/> — they simulate exactly the cold /
/// post-restart index that produced the live false-negative.
/// </summary>
public class OfferAcceptColdIndexReconcileTests
{
    private const double PickupLat = 33.5138;
    private const double PickupLng = 36.2765;

    // (a) A LIVE / pending offer accepts even though the in-memory routing index is
    // EMPTY (post-restart). The gateway reconciles the pairing from offer-service and
    // returns 200 — no false "offer no longer available".
    [Fact]
    public async Task Accept_ColdIndex_LiveOffer_ReconcilesFromOfferService_Returns200()
    {
        var offerFake = new ReconcilingOfferClient();
        var deliveryFake = new StubDeliveryClient { ActiveDeliveryCount = 0 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedPendingRequestAsync(factory, "client-owner");
        // The offer is LIVE in offer-service on the owner's request — but the routing
        // index is deliberately NOT seeded (cold gateway).
        offerFake.SeedLiveOffer(requestId, offerId: "offer-cold", jeeberId: "jeeber-cold", status: "pending");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-cold/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "a live offer must accept even with a cold routing index");
        var body = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        body!.Id.Should().Be(requestId);
        body.Status.Should().Be("accepted");

        // The availability truth came from offer-service (the owner-scoped list was read),
        // and the accept saga was forwarded with the reconciled requestId.
        offerFake.ListForRequestCallCount.Should().BeGreaterThanOrEqualTo(1);
        offerFake.AcceptCallCount.Should().Be(1);
        offerFake.LastAcceptRequestId.Should().Be(requestId);
    }

    // (c) Survives a gateway restart: the FIRST accept reconciles from offer-service and
    // RE-HYDRATES the routing index; a subsequent resolve of the same offer hits the fast
    // path with NO further offer-service list read (no in-memory dependence for the
    // availability decision — it is recovered from the authoritative source once, then cached).
    [Fact]
    public async Task Accept_ColdIndex_RehydratesRoutingIndex_FastPathOnSecondResolve()
    {
        var offerFake = new ReconcilingOfferClient();
        var deliveryFake = new StubDeliveryClient { ActiveDeliveryCount = 0 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedPendingRequestAsync(factory, "client-owner");
        offerFake.SeedLiveOffer(requestId, "offer-rehydrate", "jeeber-x", "pending");

        var first = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-rehydrate/accept", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        offerFake.ListForRequestCallCount.Should().BeGreaterThanOrEqualTo(1);

        // The pairing must now be in the routing index (re-hydrated from the authoritative pair).
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.ResolveRequestId("offer-rehydrate").Should().Be(requestId);
        index.ResolveJeeberId("offer-rehydrate").Should().Be("jeeber-x");

        // A second resolve takes the FAST path — the re-hydrated index serves the routing
        // WITHOUT another owner-list RECONCILIATION scan. (The accepted-fee snapshot in
        // BuildAcceptedResponseAsync always reads the offers list once via
        // UpstreamPendingOffersStore, so each accept adds exactly ONE list call; a cold
        // re-reconciliation would add a SECOND. Asserting the delta is exactly 1 proves the
        // fast path served the availability resolution — no in-memory-loss re-lookup.)
        var listCallsAfterFirst = offerFake.ListForRequestCallCount;
        var second = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-rehydrate/accept", content: null);
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        offerFake.ListForRequestCallCount.Should().Be(
            listCallsAfterFirst + 1,
            "the re-hydrated index must serve the second resolve with no extra reconciliation scan (only the fee-snapshot list read)");
    }

    // (b1) A genuinely NON-PENDING offer still rejects: the offer IS resolvable (index warm
    // OR reconciled) but offer-service's accept saga is the authority and returns Conflict —
    // the gateway forwards it as 409 offer-not-acceptable. Real NotPending semantics preserved.
    [Fact]
    public async Task Accept_ColdIndex_GenuinelyNonPending_SagaConflict_Returns409()
    {
        var offerFake = new ReconcilingOfferClient
        {
            AcceptOverride = new OfferAcceptResult { Status = OfferAcceptStatus.Conflict },
        };
        var deliveryFake = new StubDeliveryClient { ActiveDeliveryCount = 0 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedPendingRequestAsync(factory, "client-owner");
        // The offer is still LISTED (reconcilable) but is no longer acceptable upstream.
        offerFake.SeedLiveOffer(requestId, "offer-stale", "jeeber-stale", "accepted");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-stale/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-acceptable");
        offerFake.AcceptCallCount.Should().Be(1, "a resolvable-but-non-pending offer is forwarded to the authoritative saga");
    }

    // (b2) A genuinely GONE offer (present in no owner list, cold index) resolves to a
    // correct 404 — NOT a fabricated success. This is a real reject, not a false accept.
    [Fact]
    public async Task Accept_ColdIndex_UnknownOffer_ReturnsNotFound_WithoutForwardingSaga()
    {
        var offerFake = new ReconcilingOfferClient();
        var deliveryFake = new StubDeliveryClient { ActiveDeliveryCount = 0 };
        using var factory = NewFactory(offerFake, deliveryFake);

        // The owner has an open auction, but the offer being accepted exists on NO request.
        await SeedPendingRequestAsync(factory, "client-owner");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-ghost/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        offerFake.AcceptCallCount.Should().Be(0, "an offer that exists nowhere must never be forwarded to the accept saga");
    }

    // BR-10 stays intact THROUGH cold reconciliation: the winning jeeber resolved from the
    // authoritative offer-service list is checked against the active-delivery cap BEFORE the
    // saga is forwarded — a jeeber at cap is 409'd even on the cold path.
    [Fact]
    public async Task Accept_ColdIndex_WinningJeeberAtCap_Returns409_AndDoesNotForwardSaga()
    {
        var offerFake = new ReconcilingOfferClient();
        var deliveryFake = new StubDeliveryClient { ActiveDeliveryCount = 2 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedPendingRequestAsync(factory, "client-owner");
        offerFake.SeedLiveOffer(requestId, "offer-capcold", "jeeber-busy", "pending");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-capcold/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/too-many-active-deliveries");
        // The cap was checked against the reconciled winning jeeber, and the saga never ran.
        deliveryFake.LastCountedJeeberId.Should().Be("jeeber-busy");
        offerFake.AcceptCallCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

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

    private static async Task<string> SeedPendingRequestAsync(
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
        return created.Id; // freshly created → status "pending" (an open auction)
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer"); // → contract client
        return c;
    }

    private sealed record DeliveryRequestDto(
        string Id,
        string ClientId,
        string Status,
        string Description,
        string? PickupAddress,
        string? DropoffAddress,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ScheduledAt,
        string? JeeberId,
        DateTimeOffset? AcceptedAt);

    /// <summary>
    /// Offer-service double that implements BOTH the accept seam and the authoritative
    /// owner-scoped list seam the cold-index reconciliation reads. Live offers are keyed
    /// by requestId; <see cref="ListForRequestAsync"/> returns them (as the real
    /// owner-scoped route does), and the accept returns <see cref="AcceptOverride"/> when
    /// set (else a canned Accepted envelope carrying the seeded winning jeeber).
    /// </summary>
    private sealed class ReconcilingOfferClient : IOfferServiceClient
    {
        private readonly ConcurrentDictionary<string, List<OfferWire>> _byRequestId = new(StringComparer.Ordinal);

        /// <summary>When set, every accept returns this outcome verbatim (for the non-pending test).</summary>
        public OfferAcceptResult? AcceptOverride { get; init; }

        public int AcceptCallCount { get; private set; }
        public string? LastAcceptRequestId { get; private set; }
        public int ListForRequestCallCount { get; private set; }

        public void SeedLiveOffer(string requestId, string offerId, string jeeberId, string status)
        {
            var list = _byRequestId.GetOrAdd(requestId, _ => new List<OfferWire>());
            list.Add(new OfferWire
            {
                Id = offerId,
                RequestId = requestId,
                JeeberId = jeeberId,
                Status = status,
                FeeCents = 750,
                EtaMinutes = 20,
            });
        }

        public Task<IReadOnlyList<OfferWire>> ListForRequestAsync(
            string actingUserId, string requestId, CancellationToken ct)
        {
            ListForRequestCallCount++;
            var list = _byRequestId.TryGetValue(requestId, out var offers)
                ? (IReadOnlyList<OfferWire>)offers.ToList()
                : Array.Empty<OfferWire>();
            return Task.FromResult(list);
        }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            AcceptCallCount++;
            LastAcceptRequestId = requestId;
            if (AcceptOverride is not null)
            {
                return Task.FromResult(AcceptOverride);
            }

            var jeeberId = _byRequestId.TryGetValue(requestId, out var offers)
                ? offers.FirstOrDefault(o => string.Equals(o.Id, offerId, StringComparison.Ordinal))?.JeeberId
                : null;

            return Task.FromResult(new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = offerId,
                    JeeberId = jeeberId,
                    RejectedOfferIds = Array.Empty<string>(),
                },
            });
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
    /// Minimal delivery-service double: reports <see cref="ActiveDeliveryCount"/> for the
    /// BR-10 cap read and accepts the post-accept assignment mirror. Every other member
    /// throws — the accept path must not call them.
    /// </summary>
    private sealed class StubDeliveryClient : IDeliveryServiceClient
    {
        public int ActiveDeliveryCount { get; init; }
        public string? LastCountedJeeberId { get; private set; }

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
        {
            LastCountedJeeberId = jeeberId;
            return Task.FromResult(ActiveDeliveryCount);
        }

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => Task.FromResult(new DeliveryRowUpstream { Id = body.Id, TenantId = body.TenantId, Status = "Ordered" });

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
    }
}
