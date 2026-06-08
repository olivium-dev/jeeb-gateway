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
                    ChatThreadId = "thread-9",
                    OtpCode = "4321",
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
                    ChatThreadId = "thread-legit",
                    OtpCode = "1357",
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
                    ChatThreadId = "thread-nb",
                    OtpCode = "2468",
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
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewUpstreamFactory(IOfferServiceClient fake)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
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
    }
}
