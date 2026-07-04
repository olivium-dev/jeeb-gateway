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
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// fix/client-visibility — run-22 audit items P0 (client list visibility of accepted
/// work) and P1 (delivery amount/jeeberName must persist on reads in ALL states,
/// including terminal Done, for BOTH parties).
///
/// <para><b>P0</b>: after a jeeber accepts, the OWNING client's list surfaces must all
/// include the accepted request/delivery — <c>GET /v1/requests?role=client</c>, the flat
/// <c>GET /requests</c>, <c>GET /v1/deliveries</c>, and the flat
/// <c>GET /deliveries?stage=active</c>. The last one previously forwarded the mobile
/// BUCKET alias <c>stage=active</c> verbatim to delivery-service, whose stages are
/// Ordered/Picked/InTransit/AtDoor/Done — "active" matches nothing upstream, so the
/// client's In-Progress bucket depended entirely on upstream leniency. The gateway now
/// resolves the alias itself (fetch unfiltered, keep canonical non-terminal), and the
/// participation scoping is the UNION of client-owned and jeeber-assigned rows —
/// symmetric with the /v1 jobs view.</para>
///
/// <para><b>P1</b>: the <c>amount</c> enrichment re-resolved the accepted offer from the
/// offers store on every read. On the live upstream wire that lookup is OWNER-scoped
/// (offer-service 403s any non-owner → the assigned jeeber never saw the fee) and stops
/// matching once the offer's upstream state collapses out of "accepted" after
/// completion — the $0.00 receipt. The accept orchestration now SNAPSHOTS the accepted
/// fee onto the delivery row (<see cref="DeliveryRequest.AcceptedFee"/>) and the read
/// enrichment falls back to it, so the receipt keeps its amount in every state even when
/// the live offers lookup returns nothing.</para>
/// </summary>
public class ClientVisibilityAndReceiptTests
{
    private const string AppId = "jeeb-test-app";

    // =========================================================================
    // P0 — the owning client's list surfaces include the accepted work
    // =========================================================================

    [Fact]
    public async Task AfterAccept_OwningClientLists_IncludeTheAcceptedRequestAndDelivery()
    {
        var delivery = new RecordingDeliveryClient();
        using var factory = Factory(delivery);

        // Real bearers: the flat GET /deliveries route is [Authorize] (bearer-only).
        var http = factory.CreateClient();
        var (clientToken, clientId) = await MintSession(http, "+9613100001");
        var (jeeberToken, jeeberId) = await MintSession(http, "+9613100002");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var offers = factory.Services.GetRequiredService<IPendingOffersStore>();

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "visibility parcel",
        }, CancellationToken.None);

        var offer = await offers.TrySubmitAsync(
            created.Id, jeeberId, fee: 12m, etaMinutes: 10, note: "RUN22 NOTE",
            maxPerRequest: 20, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        // The OWNING client accepts through the V1 route the mobile app calls.
        var acceptResp = await HeaderClient(factory, clientId, "customer")
            .PostAsync($"/v1/offers/{offer.Id}/accept", null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // ---- GET /v1/requests?role=client — the Orders tab ----------------------
        var ordersResp = await HeaderClient(factory, clientId, "customer")
            .GetAsync("/v1/requests?role=client&page=1&pageSize=50");
        ordersResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var orders = await ordersResp.Content.ReadFromJsonAsync<PagedEnvelope>();
        orders!.Items.Should().Contain(i => i.Id == created.Id,
            "the client's own accepted request must stay visible on the role=client list");
        orders.Items.Single(i => i.Id == created.Id).Status.Should().Be("Ordered",
            "accepted surfaces as the canonical post-accept token");

        // ---- flat GET /requests — the legacy client history surface -------------
        var flatResp = await HeaderClient(factory, clientId, "customer").GetAsync("/requests");
        flatResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var flatRows = await flatResp.Content.ReadFromJsonAsync<List<FlatRequestRow>>();
        flatRows.Should().Contain(r => r.Id == created.Id,
            "the flat owner-scoped list must include the accepted request");

        // ---- GET /v1/deliveries — client leg of the union ------------------------
        var v1DeliveriesResp = await HeaderClient(factory, clientId, "customer")
            .GetAsync("/v1/deliveries");
        v1DeliveriesResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var v1Deliveries = await v1DeliveriesResp.Content.ReadFromJsonAsync<PagedEnvelope>();
        v1Deliveries!.Items.Should().Contain(i => i.Id == created.Id,
            "the accepted (active) delivery is client-owned and must list for the client");

        // ---- flat GET /deliveries?stage=active — the In-Progress bucket ---------
        // The fake upstream mimics delivery-service semantics: a stage filter is an
        // EXACT stage-token match, so a verbatim-forwarded `stage=active` matches
        // nothing. The gateway must resolve the bucket alias itself.
        delivery.Shipments = new List<ShipmentDetailDto>
        {
            new() { Id = "shp-owned", OrderId = created.Id, CurrentStage = "Ordered" },
            new() { Id = "shp-foreign", OrderId = "someone-elses-order", CurrentStage = "Ordered" },
            new() { Id = "shp-done", OrderId = created.Id, CurrentStage = "Done" },
        };

        var bucket = await BearerGet(http, clientToken, "/deliveries?stage=active&limit=50");
        bucket.StatusCode.Should().Be(HttpStatusCode.OK);
        var bucketBody = await bucket.Content.ReadFromJsonAsync<ShipmentsEnvelope>();
        bucketBody!.Shipments.Should().ContainSingle(s => s.Id == "shp-owned",
            "the owning client's active shipment must appear in the stage=active bucket");
        bucketBody.Shipments.Should().NotContain(s => s.Id == "shp-foreign",
            "foreign shipments stay scoped out (fail closed)");
        bucketBody.Shipments.Should().NotContain(s => s.Id == "shp-done",
            "terminal shipments are not 'active'");
        delivery.ListStageArgs.Should().Contain((string?)null,
            "the gateway resolves the mobile bucket alias itself instead of forwarding 'active' upstream");
        delivery.ListStageArgs.Should().NotContain("active");

        // ---- symmetric party scoping: the assigned JEEBER sees it too -----------
        var jeeberBucket = await BearerGet(http, jeeberToken, "/deliveries?stage=active&limit=50");
        jeeberBucket.StatusCode.Should().Be(HttpStatusCode.OK);
        var jeeberBody = await jeeberBucket.Content.ReadFromJsonAsync<ShipmentsEnvelope>();
        jeeberBody!.Shipments.Should().ContainSingle(s => s.Id == "shp-owned",
            "the assigned jeeber participates in the delivery and must see it on the legacy surface too");
    }

    [Fact]
    public async Task StageActiveBucket_ExplicitCanonicalStage_StillForwardsVerbatim()
    {
        var delivery = new RecordingDeliveryClient();
        using var factory = Factory(delivery);
        var http = factory.CreateClient();
        var (token, userId) = await MintSession(http, "+9613100003");

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "verbatim stage",
        }, CancellationToken.None);
        delivery.Shipments = new List<ShipmentDetailDto>
        {
            new() { Id = "shp-picked", OrderId = created.Id, CurrentStage = "Picked" },
        };

        var resp = await BearerGet(http, token, "/deliveries?stage=Picked");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ShipmentsEnvelope>();
        body!.Shipments.Should().ContainSingle(s => s.Id == "shp-picked");
        delivery.ListStageArgs.Should().Contain("Picked",
            "a real canonical stage token keeps forwarding verbatim — only the bucket alias is resolved gateway-side");
    }

    // =========================================================================
    // P1 — amount + jeeberName persist through the FULL lifecycle to Done
    // =========================================================================

    [Fact]
    public async Task GetById_AfterFullLifecycleToDone_StillCarriesAmountAndJeeberName_ForBothParties()
    {
        var delivery = new RecordingDeliveryClient();
        var offers = new FlippablePendingOffersStore(new InMemoryPendingOffersStore(TimeProvider.System));
        using var factory = Factory(delivery, offers);

        var clientId = $"client-{Guid.NewGuid():N}";
        var jeeberId = $"jeeber-{Guid.NewGuid():N}";
        const decimal agreedFee = 12.34m;

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var users = factory.Services.GetRequiredService<IUsersStore>();

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "receipt parcel",
        }, CancellationToken.None);

        var offer = await offers.TrySubmitAsync(
            created.Id, jeeberId, fee: agreedFee, etaMinutes: 15, note: null,
            maxPerRequest: 20, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        await users.UpsertProjectionAsync(new UserProfile
        {
            Id = jeeberId,
            Phone = "+9613100999",
            Name = "Karim Jeeber",
        }, CancellationToken.None);

        // Accept through the V1 route (in-memory auction path) — stamps the fee snapshot.
        var acceptResp = await HeaderClient(factory, clientId, "customer")
            .PostAsync($"/v1/offers/{offer.Id}/accept", null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await store.GetAsync(created.Id, CancellationToken.None);
        row!.AcceptedFee.Should().Be(agreedFee, "the accept orchestration snapshots the agreed fee onto the row");

        // Drive the FULL canonical lifecycle through the PATCH surface the app uses.
        var jeeberHttp = HeaderClient(factory, jeeberId, "driver");
        foreach (var to in new[] { "Picked", "InTransit", "AtDoor", "Done" })
        {
            var patch = await jeeberHttp.PatchAsync(
                $"/deliveries/{created.Id}/status", JsonContent.Create(new { to }));
            patch.StatusCode.Should().Be(HttpStatusCode.OK, $"the {to} transition must commit");
        }

        // Simulate the live wire AFTER completion: the offers-store read no longer
        // resolves the accepted offer (owner-scoped 403 for the jeeber; terminal
        // offer-state collapse for everyone). The snapshot must carry the receipt.
        offers.ReturnEmptyLists = true;

        // The CLIENT reads the receipt after Done.
        var clientRead = await HeaderClient(factory, clientId, "customer")
            .GetAsync($"/v1/deliveries/{created.Id}");
        clientRead.StatusCode.Should().Be(HttpStatusCode.OK);
        var clientDto = await clientRead.Content.ReadFromJsonAsync<DeliveryReadDto>();
        clientDto!.Status.Should().Be("Done", "the mirror reflects the completed canonical state");
        clientDto.Amount.Should().Be(agreedFee,
            "the receipt is read AFTER completion by definition — amount must survive Done");
        clientDto.JeeberName.Should().Be("Karim Jeeber");

        // The assigned JEEBER's read carries the agreed amount too (party symmetry —
        // on the live wire the owner-scoped offers lookup 403s the jeeber, which
        // previously left the jeeber's own delivery reads without any amount).
        var jeeberRead = await jeeberHttp.GetAsync($"/v1/deliveries/{created.Id}");
        jeeberRead.StatusCode.Should().Be(HttpStatusCode.OK);
        var jeeberDto = await jeeberRead.Content.ReadFromJsonAsync<DeliveryReadDto>();
        jeeberDto!.Amount.Should().Be(agreedFee);
        jeeberDto.JeeberName.Should().Be("Karim Jeeber");
    }

    [Fact]
    public async Task UpstreamAccept_SnapshotsAcceptedFee_FromTheOwnerScopedOffersRead()
    {
        // The PRODUCTION path: FeatureFlags:UseUpstream:Offer = true — the accept is
        // committed by offer-service and the gateway's BuildAcceptedResponseAsync
        // orchestration must resolve the accepted offer's fee (the acceptor IS the
        // owner, so the owner-scoped read is authorized at exactly this moment) and
        // snapshot it for all later reads.
        var delivery = new RecordingDeliveryClient();
        var offers = new FlippablePendingOffersStore(new InMemoryPendingOffersStore(TimeProvider.System));
        var offerService = new AcceptingOfferServiceClient();

        var clientId = $"client-{Guid.NewGuid():N}";
        var jeeberId = $"jeeber-{Guid.NewGuid():N}";
        const string offerId = "up-offer-1";
        const decimal agreedFee = 15.50m;

        using var factory = Factory(delivery, offers, upstreamOffer: true, offerService: offerService);

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "upstream accept parcel",
        }, CancellationToken.None);

        offerService.Envelope = new OfferAcceptWire
        {
            AcceptedOfferId = offerId,
            JeeberId = jeeberId,
            RejectedOfferIds = Array.Empty<string>(),
        };
        // The owner-scoped offers list serves the accepted bid at accept time.
        offers.ListOverride = _ => new List<PendingOffer>
        {
            new()
            {
                Id = offerId,
                RequestId = created.Id,
                JeeberId = jeeberId,
                Status = PendingOfferStatus.Accepted,
                CreatedAt = DateTimeOffset.UtcNow,
                Fee = agreedFee,
                EtaMinutes = 9,
            },
        };

        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record(offerId, created.Id, jeeberId);

        var acceptResp = await HeaderClient(factory, clientId, "customer")
            .PostAsync($"/v1/offers/{offerId}/accept", null);
        acceptResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var row = await store.GetAsync(created.Id, CancellationToken.None);
        row!.AcceptedFee.Should().Be(agreedFee,
            "the upstream accept path snapshots the accepted offer's fee onto the local row");

        // After completion the offers read yields nothing — the snapshot carries the amount.
        offers.ListOverride = _ => Array.Empty<PendingOffer>();
        var read = await HeaderClient(factory, jeeberId, "driver").GetAsync($"/v1/deliveries/{created.Id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await read.Content.ReadFromJsonAsync<DeliveryReadDto>();
        dto!.Amount.Should().Be(agreedFee);
    }

    [Fact]
    public async Task GetById_WithNoAcceptedOfferAndNoSnapshot_StillOmitsAmount()
    {
        // The additive/ignore-when-null contract is unchanged: no accepted offer and
        // no snapshot ⇒ no amount key at all (never a fabricated 0).
        var delivery = new RecordingDeliveryClient();
        using var factory = Factory(delivery);
        var clientId = $"client-{Guid.NewGuid():N}";

        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "no offer yet",
        }, CancellationToken.None);

        var resp = await HeaderClient(factory, clientId, "customer").GetAsync($"/v1/deliveries/{created.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().NotContain("\"amount\"");
    }

    // =========================================================================
    // helpers
    // =========================================================================

    private static WebApplicationFactory<Program> Factory(
        RecordingDeliveryClient delivery,
        IPendingOffersStore? offersStore = null,
        bool upstreamOffer = false,
        IOfferServiceClient? offerService = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    { "FeatureFlags:UseUpstream:Offer", upstreamOffer ? "true" : "false" },
                    { "FeatureFlags:UseUpstream:Delivery", "false" },
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(delivery);

                if (offersStore is not null)
                {
                    services.RemoveAll<IPendingOffersStore>();
                    services.AddSingleton(offersStore);
                }

                if (offerService is not null)
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(offerService);
                }

                // Real-session mint for the [Authorize]-gated flat GET /deliveries.
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton<IServiceOTPClient>(new StubOtpClient());
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.Offer = upstreamOffer;
                    f.Delivery = false;
                });
                services.Configure<JeebGateway.Auth.OtpSignIn.OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });
            });
        });

    private static HttpClient HeaderClient(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static async Task<(string Token, string UserId)> MintSession(HttpClient http, string phone)
    {
        var resp = await http.PostAsJsonAsync("/v1/auth/otp/verify", new { phone, code = "1234" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "the OTP verify path mints a real session");
        var json = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        return (
            json.GetProperty("accessToken").GetString()!,
            json.GetProperty("user").GetProperty("userId").GetString()!);
    }

    private static Task<HttpResponseMessage> BearerGet(HttpClient http, string token, string url)
    {
        var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return http.SendAsync(msg);
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

    // ----- response shapes ----------------------------------------------------

    private sealed class PagedEnvelope
    {
        [JsonPropertyName("items")] public List<ListItem> Items { get; set; } = new();
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    private sealed class ListItem
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("jeeberId")] public string? JeeberId { get; set; }
    }

    private sealed class FlatRequestRow
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
    }

    private sealed class ShipmentsEnvelope
    {
        [JsonPropertyName("shipments")] public List<ShipmentRow> Shipments { get; set; } = new();
        [JsonPropertyName("count")] public int Count { get; set; }
    }

    private sealed class ShipmentRow
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("orderId")] public string? OrderId { get; set; }
        [JsonPropertyName("currentStage")] public string? CurrentStage { get; set; }
    }

    private sealed class DeliveryReadDto
    {
        [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("amount")] public decimal? Amount { get; set; }
        [JsonPropertyName("jeeberName")] public string? JeeberName { get; set; }
    }

    // ----- fakes ---------------------------------------------------------------

    /// <summary>
    /// Fake delivery-service. <see cref="ListShipmentsAsync"/> mimics the REAL
    /// upstream semantics: a non-null <c>stage</c> is an exact stage-token filter —
    /// so a verbatim-forwarded <c>stage=active</c> matches nothing. The canonical
    /// transition endpoint echoes the target state (the SM legality lives upstream
    /// and is not under test here).
    /// </summary>
    private sealed class RecordingDeliveryClient : IDeliveryServiceClient
    {
        public List<ShipmentDetailDto> Shipments { get; set; } = new();
        public List<string?> ListStageArgs { get; } = new();

        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct)
        {
            ListStageArgs.Add(stage);
            var rows = Shipments
                .Where(s => stage is null || string.Equals(s.CurrentStage, stage, StringComparison.Ordinal))
                .ToList();
            return Task.FromResult(new ShipmentsListDto { Shipments = rows, Count = rows.Count });
        }

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct)
            => Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = to,
                TransitionId = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow,
            });

        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct)
            => Task.FromResult(new DeliveryRowUpstream { DeliveryId = body.Id });
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct)
            => Task.FromResult(0);

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
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
    }

    /// <summary>
    /// Delegating offers store whose request-scoped list read can be made to return
    /// nothing — the exact live-wire behavior AFTER completion (owner-scoped 403 →
    /// empty degrade; terminal offer-state collapse) that the accept-time fee
    /// snapshot must survive. <see cref="ListOverride"/> substitutes a fixed list
    /// (used to model the upstream offer-service read on the flag-ON path).
    /// </summary>
    private sealed class FlippablePendingOffersStore : IPendingOffersStore
    {
        private readonly IPendingOffersStore _inner;

        public FlippablePendingOffersStore(IPendingOffersStore inner) => _inner = inner;

        public bool ReturnEmptyLists { get; set; }
        public Func<string, IReadOnlyList<PendingOffer>>? ListOverride { get; set; }

        public Task<IReadOnlyList<PendingOffer>> ListForRequestAsync(string requestId, CancellationToken ct)
        {
            if (ReturnEmptyLists)
                return Task.FromResult<IReadOnlyList<PendingOffer>>(Array.Empty<PendingOffer>());
            if (ListOverride is not null)
                return Task.FromResult(ListOverride(requestId));
            return _inner.ListForRequestAsync(requestId, ct);
        }

        public Task<PendingOffer> TrySubmitAsync(string requestId, string jeeberId, decimal fee, int etaMinutes, string? note, int maxPerRequest, DateTimeOffset at, CancellationToken ct, string? clientId = null)
            => _inner.TrySubmitAsync(requestId, jeeberId, fee, etaMinutes, note, maxPerRequest, at, ct, clientId);
        public Task<WithdrawOfferOutcome> TryWithdrawAsync(string offerId, string requestId, string jeeberId, DateTimeOffset at, CancellationToken ct)
            => _inner.TryWithdrawAsync(offerId, requestId, jeeberId, at, ct);
        public Task<int> WithdrawForJeeberAsync(string jeeberId, CancellationToken ct)
            => _inner.WithdrawForJeeberAsync(jeeberId, ct);
        public Task<PendingOffer?> GetAsync(string offerId, CancellationToken ct)
            => _inner.GetAsync(offerId, ct);
        public Task<bool> AcceptAsync(string offerId, DateTimeOffset at, CancellationToken ct)
            => _inner.AcceptAsync(offerId, at, ct);
        public Task<AcceptOfferOutcome> AcceptWithSupersedeAsync(string offerId, DateTimeOffset at, CancellationToken ct)
            => _inner.AcceptWithSupersedeAsync(offerId, at, ct);
        public Task<EditOfferOutcome> TryEditAsync(string offerId, string requestId, string jeeberId, decimal? fee, int? etaMinutes, string? note, int maxEdits, DateTimeOffset at, CancellationToken ct)
            => _inner.TryEditAsync(offerId, requestId, jeeberId, fee, etaMinutes, note, maxEdits, at, ct);
    }

    /// <summary>Offer-service fake whose accept saga always commits with the configured envelope.</summary>
    private sealed class AcceptingOfferServiceClient : IOfferServiceClient
    {
        public OfferAcceptWire? Envelope { get; set; }

        public Task<OfferAcceptResult> AcceptWithStatusAsync(string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(new OfferAcceptResult
            {
                Status = OfferAcceptStatus.Accepted,
                Envelope = Envelope,
            });

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
}
