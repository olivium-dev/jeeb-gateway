using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S07 accept-saga rewire. When <c>FeatureFlags:UseUpstream:Offer = true</c> the
/// gateway forwards <c>POST /offers/{offerId}/accept</c> to the offer-service
/// accept saga via <see cref="IOfferServiceClient.AcceptWithStatusAsync"/> and
/// re-emits the upstream status VERBATIM (200/403/410/409/404) — it must NOT
/// mask offer-service negatives as a raw 500 (the baseline bug), and must NOT
/// re-run the auction.
///
/// The offer-service is replaced by a <see cref="FakeOfferServiceClient"/> so the
/// suite asserts the gateway's status mapping deterministically without a live
/// upstream. The offerId → requestId routing pairing is seeded via the real
/// <see cref="IOfferRequestIndex"/> (exactly what RequestOffersController.Submit
/// records on a real submit).
/// </summary>
public class OfferAcceptUpstreamTests
{
    [Fact]
    public async Task Accept_Forwards_To_Upstream_And_Returns_200_On_Saga_Success()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-1",
                    JeeberId = "jeeber-9", // the awarded jeeber, from the upstream envelope
                    RejectedOfferIds = new[] { "offer-2" }
                }
            }
        };

        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, offerId: "offer-1", requestId: "req-1");

        // The acceptor is the CLIENT who owns the request, not the jeeber.
        var resp = await ClientActor(factory, "client-sami")
            .PostAsync("/offers/offer-1/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        body!.Id.Should().Be("req-1");
        body.Status.Should().Be("accepted");
        // ClientId is the acting client; JeeberId is the awarded jeeber from the envelope.
        body.ClientId.Should().Be("client-sami");
        body.JeeberId.Should().Be("jeeber-9");

        // The gateway forwarded the SAGA call with the resolved requestId, the CLIENT's
        // id as the acting user (so the upstream request-owner guard passes), and a
        // server-minted idempotency key — it did not run the auction itself.
        fake.LastRequestId.Should().Be("req-1");
        fake.LastOfferId.Should().Be("offer-1");
        fake.LastActingUserId.Should().Be("client-sami");
        fake.LastIdempotencyKey.Should().NotBeNullOrWhiteSpace();
        fake.LastIdempotencyKey!.Length.Should().BeGreaterThanOrEqualTo(8);
    }

    [Fact]
    public async Task Accept_NonOwner_Returns_403_From_Upstream_Not_500()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.NotOwner }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, "offer-403", "req-403");

        var resp = await ClientActor(factory, "client-mallory")
            .PostAsync("/offers/offer-403/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-owned");
    }

    [Fact]
    public async Task Accept_Expired_Returns_410_From_Upstream_Not_500()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Expired }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, "offer-410", "req-410");

        var resp = await ClientActor(factory, "client-410")
            .PostAsync("/offers/offer-410/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-expired");
    }

    [Fact]
    public async Task Accept_CapOrAlreadyAccepted_Returns_409_From_Upstream_Not_500()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Conflict }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, "offer-409", "req-409");

        var resp = await ClientActor(factory, "client-409")
            .PostAsync("/offers/offer-409/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-pending");
    }

    [Fact]
    public async Task Accept_Unknown_Offer_Returns_404_Without_Calling_Upstream()
    {
        var fake = new FakeOfferServiceClient
        {
            // Should never be consulted — the routing index miss short-circuits.
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Accepted }
        };
        using var factory = NewUpstreamFactory(fake);
        // No SeedRouting → unknown offer.

        var resp = await ClientActor(factory, "client-404")
            .PostAsync($"/offers/{Guid.NewGuid()}/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        fake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Accept_AsJeeber_Is403_AtCapabilityGate_NeverReachesUpstream()
    {
        // S07 regression guard: the live H5/A6 403 happened because a JEEBER was the
        // acceptor. With offer.accept keyed {client}, a jeeber caller is rejected at
        // the L2 capability gate and the saga is NEVER forwarded.
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Accepted }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, "offer-jeeber", "req-jeeber");

        var jeeber = factory.CreateClient();
        jeeber.DefaultRequestHeaders.Add("X-User-Id", "kamal-jeeber");
        jeeber.DefaultRequestHeaders.Add("X-User-Roles", "driver");

        var resp = await jeeber.PostAsync("/offers/offer-jeeber/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // The capability filter rejected before the controller ran: the saga was never called.
        fake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Accept_Upstream_404_Returns_404()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.NotFound }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, "offer-phantom", "req-phantom");

        var resp = await ClientActor(factory, "client-phantom")
            .PostAsync("/offers/offer-phantom/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // S07 BR-1 inverted-guard regression (attempt 6 fix).
    //
    // The legitimate offer ACCEPTOR is ALWAYS the request-owning CLIENT, so
    // request.ClientId == actor is the NORMAL, correct case. The pre-fix gateway
    // BR-1 check compared actor against request.ClientId and therefore tripped a
    // 409 same-delivery-role-violation on EVERY valid accept. The fix compares the
    // actor against THIS OFFER's recorded bidder instead: a self-offer (actor is
    // both the request client AND the offer's jeeber) still 409s; an ordinary
    // client accept does NOT.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_By_Request_Owning_Client_Does_Not_Trip_BR1_409()
    {
        // The bidding jeeber is a DIFFERENT user from the accepting client — the
        // ordinary, correct case. Pre-fix this returned 409; it must now reach the
        // saga and return 200.
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-legit",
                    JeeberId = "jeeber-other",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        using var factory = NewUpstreamFactory(fake);
        // Routing records the offer's bidder (jeeber-other) — NOT the acceptor.
        SeedRouting(factory, offerId: "offer-legit", requestId: "req-legit", jeeberId: "jeeber-other");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/offers/offer-legit/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        body!.Id.Should().Be("req-legit");
        body.Status.Should().Be("accepted");
        body.ClientId.Should().Be("client-owner");
        // The saga WAS forwarded with the client's id (BR-1 did not short-circuit).
        fake.CallCount.Should().Be(1);
        fake.LastActingUserId.Should().Be("client-owner");
    }

    [Fact]
    public async Task Accept_Genuine_Self_Offer_Still_Returns_409_BR1()
    {
        // BR-1 self-dealing: the accepting CLIENT is also the JEEBER who bid the
        // offer being accepted (actor == offer.jeeberId). This is the ONLY
        // legitimate BR-1 violation on the accept path and must still 409 BEFORE
        // the saga is forwarded.
        var fake = new FakeOfferServiceClient
        {
            // Must NOT be consulted — BR-1 short-circuits before the saga call.
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Accepted }
        };
        using var factory = NewUpstreamFactory(fake);
        // The offer's recorded bidder IS the same user who is now accepting.
        SeedRouting(factory, offerId: "offer-self", requestId: "req-self", jeeberId: "dual-role-dana");

        var resp = await ClientActor(factory, "dual-role-dana")
            .PostAsync("/offers/offer-self/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/same-delivery-role-violation");
        // The saga was NEVER forwarded — BR-1 failed fast.
        fake.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Accept_With_Unknown_Bidder_Defers_BR1_To_Saga_And_Returns_200()
    {
        // When the routing index has no jeeber id for the offer (legacy 2-arg
        // Record / unknown bidder), the gateway must NOT assert a BR-1 violation —
        // it defers to the offer-service ownership guard. A normal accept proceeds.
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-nobidder",
                    JeeberId = "jeeber-from-envelope",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        using var factory = NewUpstreamFactory(fake);
        // 2-arg Record → no jeeber id recorded.
        SeedRouting(factory, offerId: "offer-nobidder", requestId: "req-nobidder");

        var resp = await ClientActor(factory, "client-nb")
            .PostAsync("/offers/offer-nobidder/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CallCount.Should().Be(1);
    }

    // -----------------------------------------------------------------
    // Retired BR-10 active-delivery cap.
    //
    // Accepting an offer still assigns the delivery to the OFFER'S jeeber, but
    // gateway accept routes no longer pre-count delivery-service active rows.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_When_OfferJeeber_Has_Two_Active_Deliveries_Forwards_Saga()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-cap",
                    JeeberId = "jeeber-busy",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        var delivery = new FakeDeliveryServiceClient { ActiveCount = 2 };
        using var factory = NewUpstreamFactory(fake, delivery);
        SeedRouting(factory, offerId: "offer-cap", requestId: "req-cap", jeeberId: "jeeber-busy");

        var resp = await ClientActor(factory, "client-cap")
            .PostAsync("/offers/offer-cap/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        delivery.LastCountedJeeberId.Should().BeNull();
        fake.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Accept_When_OfferJeeber_Has_One_Active_Delivery_Forwards_Saga_And_Returns_200()
    {
        // The active-delivery count is no longer consulted before forwarding to the saga.
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-under",
                    JeeberId = "jeeber-under",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        var delivery = new FakeDeliveryServiceClient { ActiveCount = 1 };
        using var factory = NewUpstreamFactory(fake, delivery);
        SeedRouting(factory, offerId: "offer-under", requestId: "req-under", jeeberId: "jeeber-under");

        var resp = await ClientActor(factory, "client-under")
            .PostAsync("/offers/offer-under/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        delivery.LastCountedJeeberId.Should().BeNull();
        fake.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Accept_Does_Not_Read_DeliveryService_Count_And_Returns_200()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-blip",
                    JeeberId = "jeeber-blip",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        var delivery = new FakeDeliveryServiceClient
        {
            Fault = new DeliveryActiveCountException(503, "service_unavailable")
        };
        using var factory = NewUpstreamFactory(fake, delivery);
        SeedRouting(factory, offerId: "offer-blip", requestId: "req-blip", jeeberId: "jeeber-blip");

        var resp = await ClientActor(factory, "client-blip")
            .PostAsync("/offers/offer-blip/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        delivery.LastCountedJeeberId.Should().BeNull();
        fake.CallCount.Should().Be(1);
    }

    // S07 N7 / BR-10 — the post-accept assignment mirror. A successful accept must
    // assign the WINNING jeeber onto the delivery-service row (seeded unassigned at
    // create time) so the delivery counts toward that jeeber's active-delivery cap;
    // otherwise the count stays 0 and a 3rd accept is never short-circuited (the live
    // N7 red). This is the precondition that makes "2 genuine accepts -> 2 deliveries
    // -> 3rd accept 409" real rather than faked.
    [Fact]
    public async Task Accept_Assigns_Winning_Jeeber_Onto_Delivery_Row_For_BR10()
    {
        var delivery = new FakeDeliveryServiceClient { ActiveCount = 0 };
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-assign",
                    JeeberId = "jeeber-assign-winner",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        using var factory = NewUpstreamFactory(fake, delivery);

        // A real ledger row must exist for the post-accept orchestration to resolve
        // the delivery id/client/tier/pickup it re-POSTs (the production + suite path).
        var store = factory.Services.GetRequiredService<JeebGateway.Requests.IRequestsStore>();
        var request = await store.CreateAsync(new JeebGateway.Requests.CreateRequestInput
        {
            ClientId = "client-assign",
            Description = "BR-10 assignment mirror",
            TierId = "standard",
            PickupLocation = new JeebGateway.Requests.GeoPoint { Lat = 24.71, Lng = 46.67 }
        }, CancellationToken.None);
        SeedRouting(factory, offerId: "offer-assign", requestId: request.Id, jeeberId: "jeeber-assign-winner");

        var resp = await ClientActor(factory, "client-assign")
            .PostAsync("/offers/offer-assign/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // The assignment mirror fired with the WINNING jeeber on the SAME delivery id.
        delivery.CreateRowCallCount.Should().Be(1);
        delivery.LastCreatedRow.Should().NotBeNull();
        delivery.LastCreatedRow!.JeeberId.Should().Be("jeeber-assign-winner");
        delivery.LastCreatedRow!.Id.Should().Be(request.Id);
        delivery.LastCreatedRow!.ClientId.Should().Be("client-assign");
    }

    // -----------------------------------------------------------------
    // S07 H5/A3/N7 regression guard (JEB-45 conflation) — the offer-accept DTO
    // must report the OFFER-acceptance status "accepted" EVEN WHEN
    // UseUpstream:Delivery is ON (the live fleet posture). JEB-45 leaked the
    // spawned delivery's canonical entry state ("Ordered") into this DTO via a
    // `_flags.Delivery ? Ordered : Accepted` ternary, regressing S07 H5
    // ($.status=="accepted") 47->43. The accept DTO carries offer-acceptance, not
    // delivery lifecycle state (ARCH LAW: accept DTO must not leak delivery status).
    // -----------------------------------------------------------------
    [Fact]
    public async Task Accept_With_Delivery_Upstream_On_Still_Returns_Offer_Status_Accepted()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-deliv-on",
                    JeeberId = "kamal-winner", // the awarded jeeber, from the upstream envelope
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };

        // Delivery kill-switch ON — the live fleet posture that triggered the regression.
        using var factory = NewUpstreamFactory(fake, new FakeDeliveryServiceClient(), deliveryUpstream: true);
        SeedRouting(factory, offerId: "offer-deliv-on", requestId: "req-deliv-on", jeeberId: "kamal-winner");

        var resp = await ClientActor(factory, "client-sami")
            .PostAsync("/offers/offer-deliv-on/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<DeliveryRequestDto>();
        body!.Id.Should().Be("req-deliv-on");
        // The OFFER-acceptance status — NOT the delivery's canonical "Ordered" state —
        // even though UseUpstream:Delivery is ON. (S07 H5 assertion.)
        body.Status.Should().Be("accepted");
        body.Status.Should().NotBe("Ordered");
        // S07 H5 also asserts $.jeeberId == winner.
        body.JeeberId.Should().Be("kamal-winner");
        body.ClientId.Should().Be("client-sami");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewUpstreamFactory(IOfferServiceClient fake)
        => NewUpstreamFactory(fake, new FakeDeliveryServiceClient());

    // Stub IDeliveryServiceClient so the suite is deterministic and never dials a
    // real upstream. The retired active-delivery cap means accept no longer calls
    // CountActiveDeliveriesByJeeberAsync.
    private static WebApplicationFactory<Program> NewUpstreamFactory(
        IOfferServiceClient fake, IDeliveryServiceClient delivery, bool deliveryUpstream = false)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        // JEB-45 regression guard: exercise the live fleet posture
                        // (Delivery kill-switch ON) so the accept DTO status mapping
                        // is asserted under the exact flag combination that regressed S07.
                        { "FeatureFlags:UseUpstream:Delivery", deliveryUpstream ? "true" : "false" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(delivery);
                });
            });

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId);

    // S07 BR-1 fix: also seed the offer's bidder so the accept path can detect a
    // genuine self-offer. Mirrors RequestOffersController.Submit's 3-arg Record.
    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    // S07: the acceptor is the request-owning CLIENT, so the test caller carries the
    // client role. (A jeeber accepting is now 403'd at the capability gate; that is
    // asserted in Jeb1509CapMapCleanupRouteTests.OfferAccept_NonClient_Is403.)
    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
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
    /// Test double for the offer-service typed client. Returns a canned
    /// <see cref="OfferAcceptResult"/> and records the forwarded call so the
    /// gateway's resolution + idempotency-key minting can be asserted.
    /// </summary>
    private sealed class FakeOfferServiceClient : IOfferServiceClient
    {
        public required OfferAcceptResult Result { get; init; }
        public int CallCount { get; private set; }
        public string? LastActingUserId { get; private set; }
        public string? LastRequestId { get; private set; }
        public string? LastOfferId { get; private set; }
        public string? LastIdempotencyKey { get; private set; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            CallCount++;
            LastActingUserId = actingUserId;
            LastRequestId = requestId;
            LastOfferId = offerId;
            LastIdempotencyKey = idempotencyKey;
            return Task.FromResult(Result);
        }

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException("Legacy throwing accept is not used on the upstream path.");

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException("Request-mirror is exercised in OfferServiceClientContractTests, not the accept path.");

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException("Edit is exercised in OfferMutationEndpointTests, not the accept path.");

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("Reject is exercised in OfferMutationEndpointTests, not the accept path.");
    }

    /// <summary>
    /// Delivery-service test double. CountActiveDeliveriesByJeeberAsync is retained
    /// only to prove the retired active-delivery cap no longer calls it.
    /// </summary>
    private sealed class FakeDeliveryServiceClient : IDeliveryServiceClient
    {
        public int ActiveCount { get; init; }
        public Exception? Fault { get; init; }
        public string? LastCountedJeeberId { get; private set; }

        // S07 N7 — capture the post-accept BR-10 delivery-assignment mirror so a test
        // can assert the winning jeeber is written onto the delivery row.
        public CreateDeliveryRowUpstream? LastCreatedRow { get; private set; }
        public int CreateRowCallCount { get; private set; }

        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
        {
            LastCountedJeeberId = jeeberId;
            if (Fault is not null) throw Fault;
            return Task.FromResult(ActiveCount);
        }

        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
        {
            // S07 N7 — the post-accept BR-10 assignment mirror calls this with the
            // winning jeeber on the delivery row. Record it (idempotent upsert shape:
            // echo the supplied id back) so a test can assert the assignment fired.
            CreateRowCallCount++;
            LastCreatedRow = body;
            return Task.FromResult(new DeliveryRowUpstream { DeliveryId = body.Id });
        }
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> StatusTransitionAsync(string deliveryId, string status, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct)
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
