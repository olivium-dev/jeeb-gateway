using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
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
        assignment.TierId.Should().Be(UpstreamFlashTierId,
            "post-accept upserts must repair legacy tier aliases to the upstream id");
        assignment.PickupLat.Should().Be(PickupLat);
        assignment.PickupLng.Should().Be(PickupLng);
    }

    [Fact]
    public async Task Accept_WhenCanonicalAssignmentHitsActiveCap_CompensatesAndReturns409WithoutLocalProjection()
    {
        var offerFake = AcceptedFake("offer-blip", "jeeber-win");
        // The create-time seed (JeeberId null) succeeds; only the canonical
        // assignment is refused exactly as delivery-service returns at 2/2.
        var deliveryFake = new RecordingDeliveryClient
        {
            AssignmentFailure = new DeliveryCreateRowException(409, "jeeber_at_active_delivery_cap"),
        };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-blip", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-blip/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/jeeber-active-delivery-cap");
        problem.GetProperty("deliveryReason").GetString().Should().Be("jeeber_at_active_delivery_cap");
        deliveryFake.JeeberAssignmentAttempts.Should().BeGreaterThanOrEqualTo(1);
        offerFake.CompensationCallCount.Should().Be(1);
        offerFake.LastCompensationIdempotencyKey.Should().Be(offerFake.LastAcceptIdempotencyKey);
        offerFake.LastCompensationToken.Should().Be("00000000-0000-0000-0000-000000000001");

        var local = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, CancellationToken.None);
        local.Should().NotBeNull();
        local!.Status.Should().Be(RequestStatus.Pending);
        local.JeeberId.Should().BeNull("a rejected canonical assignment must never stamp the gateway projection");
    }

    [Fact]
    public async Task Accept_WhenCanonicalAssignmentIsUnavailable_CompensatesAndReturns502NotSuccess()
    {
        var offerFake = AcceptedFake("offer-assignment-outage", "jeeber-win");
        var deliveryFake = new RecordingDeliveryClient
        {
            AssignmentFailure = new DeliveryCreateRowException(503, "delivery-service unavailable"),
        };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-assignment-outage", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-assignment-outage/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be(
            "https://jeeb.dev/errors/canonical-delivery-assignment-unavailable");
        offerFake.CompensationCallCount.Should().Be(1);

        var local = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, CancellationToken.None);
        local!.Status.Should().Be(RequestStatus.Pending);
        local.JeeberId.Should().BeNull();
    }

    [Fact]
    public async Task Accept_WhenCanonicalAssignmentAuthenticationFails_CompensatesAndReturns502NotConflict()
    {
        var offerFake = AcceptedFake("offer-assignment-auth", "jeeber-win");
        var deliveryFake = new RecordingDeliveryClient
        {
            AssignmentFailure = new DeliveryCreateRowException(403, "forbidden"),
        };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-assignment-auth", requestId, "jeeber-win");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-assignment-auth/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be(
            "https://jeeb.dev/errors/canonical-delivery-assignment-unavailable");
        problem.GetProperty("deliveryStatus").GetInt32().Should().Be(403);
        offerFake.CompensationCallCount.Should().Be(1);

        var local = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, CancellationToken.None);
        local!.Status.Should().Be(RequestStatus.Pending);
        local.JeeberId.Should().BeNull();
    }

    [Fact]
    public async Task Accept_WhenEnvelopeAndIndexOmitJeeber_CompensatesAndDoesNotReportSuccess()
    {
        // The winner is unresolvable anywhere — the envelope carried no jeeber id AND
        // the routing index recorded none (2-arg submit).  This must not be reported
        // as an accepted offer: the gateway cannot make the canonical assignment, so
        // it compensates the exact offer acceptance instead.
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

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be(
            "https://jeeb.dev/errors/canonical-delivery-assignment-refused");
        problem.GetProperty("deliveryReason").GetString().Should().Be("winning_jeeber_missing");
        deliveryFake.Calls.Should().NotContain(c => c.JeeberId != null);
        offerFake.CompensationCallCount.Should().Be(1);

        var local = await factory.Services.GetRequiredService<IRequestsStore>()
            .GetAsync(requestId, CancellationToken.None);
        local!.Status.Should().Be(RequestStatus.Pending);
        local.JeeberId.Should().BeNull();
    }

    [Fact]
    public async Task Accept_UsesCanonicalAssignmentInsteadOfGatewayPrecount()
    {
        var offerFake = AcceptedFake("offer-under", "jeeber-ok");
        var deliveryFake = new RecordingDeliveryClient { ActiveDeliveryCount = 1 };
        using var factory = NewFactory(offerFake, deliveryFake);

        var requestId = await SeedRequestAsync(factory, "client-owner");
        SeedRouting(factory, "offer-under", requestId, "jeeber-ok");

        var resp = await ClientActor(factory, "client-owner")
            .PostAsync("/v1/offers/offer-under/accept", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        deliveryFake.LastCountedJeeberId.Should().BeNull();
        offerFake.AcceptCallCount.Should().Be(1);
        offerFake.CompensationCallCount.Should().Be(0);
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
                    AcceptanceToken = "00000000-0000-0000-0000-000000000001",
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
        public OfferAcceptCompensationResult CompensationResult { get; init; } = new()
        {
            Status = OfferAcceptCompensationStatus.Compensated,
        };
        public int AcceptCallCount { get; private set; }
        public int CompensationCallCount { get; private set; }
        public string? LastAcceptIdempotencyKey { get; private set; }
        public string? LastCompensationIdempotencyKey { get; private set; }
        public string? LastCompensationToken { get; private set; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
        {
            AcceptCallCount++;
            LastAcceptIdempotencyKey = idempotencyKey;
            return Task.FromResult(Result);
        }

        public Task<OfferAcceptCompensationResult> CompensateAcceptedOfferAsync(
            string actingUserId, string requestId, string offerId, string acceptIdempotencyKey,
            string? acceptanceToken, CancellationToken ct)
        {
            CompensationCallCount++;
            LastCompensationIdempotencyKey = acceptIdempotencyKey;
            LastCompensationToken = acceptanceToken;
            return Task.FromResult(CompensationResult);
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
        public DeliveryCreateRowException? AssignmentFailure { get; init; }
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
                if (AssignmentFailure is not null)
                    throw AssignmentFailure;
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
