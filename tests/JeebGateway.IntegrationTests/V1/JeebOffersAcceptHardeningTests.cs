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

namespace JeebGateway.IntegrationTests.V1;

/// <summary>
/// JEBV4-83 (F5/F6) — hardening on the LIVE V1 accept surface
/// <c>POST /v1/offers/{id}/accept</c> (JeebOffersController.AcceptUpstreamAsync),
/// the route the mobile app actually calls. Previously only the legacy
/// <c>/offers/{id}/accept</c> route carried the BR-1 self-offer guard and a
/// deterministic idempotency key, so the two accept surfaces diverged.
///
/// F5 — a client self-accepting their own offer via /v1 must 409
/// <c>same-delivery-role-violation</c> BEFORE the saga is forwarded (defense-in-depth,
/// mirroring OffersController.AcceptViaUpstreamAsync:553-564).
/// F6 — when the caller omits <c>Idempotency-Key</c>, the gateway must forward the
/// stable <c>accept-{actorId}-{offerId}</c> key (not a fresh Guid per attempt), so an
/// accept retry replays rather than re-running the accept side-effects.
///
/// The offer-service is replaced by a <see cref="FakeOfferServiceClient"/> so the
/// gateway's BR-1 short-circuit and forwarded idempotency key are asserted
/// deterministically without a live upstream; delivery-service is stubbed so the suite
/// never dials a real upstream.
/// </summary>
public class JeebOffersAcceptHardeningTests
{
    // F5 — genuine self-offer: the accepting client IS the jeeber who bid this offer.
    [Fact]
    public async Task V1Accept_Genuine_Self_Offer_Returns_409_BR1_Before_Saga()
    {
        var fake = new FakeOfferServiceClient
        {
            // Must NOT be consulted — the BR-1 guard short-circuits before the saga call.
            Result = new OfferAcceptResult { Status = OfferAcceptStatus.Accepted }
        };
        using var factory = NewUpstreamFactory(fake);
        // The offer's recorded bidder IS the same user who is now accepting.
        SeedRouting(factory, offerId: "offer-self", requestId: "req-self", jeeberId: "dual-role-dana");

        var resp = await ClientActor(factory, "dual-role-dana")
            .PostAsync("/v1/offers/offer-self/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/same-delivery-role-violation");
        // The saga was NEVER forwarded — BR-1 failed fast on the V1 surface too.
        fake.CallCount.Should().Be(0);
    }

    // F5 (negative) — an ordinary client accepting a DIFFERENT jeeber's offer must NOT
    // trip BR-1: it reaches the saga (the guard compares against the offer's bidder,
    // never request.ClientId, which would trip on every valid accept).
    [Fact]
    public async Task V1Accept_By_Request_Owning_Client_Does_Not_Trip_BR1()
    {
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
        SeedRouting(factory, offerId: "offer-legit", requestId: "req-legit", jeeberId: "jeeber-other");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-legit/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CallCount.Should().Be(1);
        fake.LastActingUserId.Should().Be("client-owner");
    }

    // F6 — no Idempotency-Key header: the gateway forwards the DETERMINISTIC
    // accept-{actorId}-{offerId} key so a retry replays instead of double-applying.
    [Fact]
    public async Task V1Accept_Without_Header_Forwards_Deterministic_Idempotency_Key()
    {
        var fake = new FakeOfferServiceClient
        {
            Result = new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = new OfferAcceptWire
                {
                    AcceptedOfferId = "offer-idem",
                    JeeberId = "jeeber-idem",
                    RejectedOfferIds = Array.Empty<string>()
                }
            }
        };
        using var factory = NewUpstreamFactory(fake);
        SeedRouting(factory, offerId: "offer-idem", requestId: "req-idem", jeeberId: "jeeber-idem");

        // No Idempotency-Key header on the request.
        var resp = await ClientActor(factory, "client-idem")
            .PostAsync("/v1/offers/offer-idem/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        fake.CallCount.Should().Be(1);
        // Deterministic per (actor, offer) — a retry mints the SAME key and dedupes,
        // never a fresh Guid.NewGuid().ToString("N").
        fake.LastIdempotencyKey.Should().Be("accept-client-idem-offer-idem");
        fake.LastIdempotencyKey!.Length.Should().BeGreaterThanOrEqualTo(8);
        Guid.TryParseExact(fake.LastIdempotencyKey, "N", out _)
            .Should().BeFalse("a fabricated Guid('N') key is exactly the retry-replay bug F6 fixes");
    }

    // -----------------------------------------------------------------
    // Helpers (mirror OfferAcceptUpstreamTests' proven upstream harness)
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewUpstreamFactory(IOfferServiceClient fake)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        { "FeatureFlags:UseUpstream:Offer", "true" },
                        { "FeatureFlags:UseUpstream:Delivery", "false" }
                    }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton<IDeliveryServiceClient>(new FakeDeliveryServiceClient());
                });
            });

    private static void SeedRouting(
        WebApplicationFactory<Program> factory, string offerId, string requestId, string jeeberId)
        => factory.Services.GetRequiredService<IOfferRequestIndex>().Record(offerId, requestId, jeeberId);

    // The V1 accept caller is the request-owning CLIENT (offer.accept {client}).
    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    /// <summary>
    /// Test double for the offer-service typed client — records the forwarded accept
    /// call so the BR-1 short-circuit (never forwarded) and the deterministic
    /// idempotency key can be asserted. Inherits the interface's safe empty defaults
    /// for the list methods.
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
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Delivery-service test double — every member throws so the suite proves the
    /// accept path never dials delivery-service on these routes (req is unseeded, so
    /// the delivery-leg sync early-returns before any call).
    /// </summary>
    private sealed class FakeDeliveryServiceClient : IDeliveryServiceClient
    {
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<JeebGateway.Tiers.DeliveryTierDto>> ListTiersAsync(CancellationToken ct)
            => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => throw new NotSupportedException();
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
