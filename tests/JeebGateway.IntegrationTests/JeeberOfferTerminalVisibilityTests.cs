using System;
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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// fix/offer-visibility (run-23 CHECK C, real-device proven): after the customer accepts
/// jeeber A's offer, jeeber B's OWN offer-list surfaces must keep serving B's offer in its
/// terminal state — previously they returned 404/empty because:
///
/// <list type="bullet">
///   <item><c>GET /v1/jeebers/me/offers</c> did not exist on the gateway at all (404);</item>
///   <item><c>GET /v1/offers?jeeberId=&lt;me&gt;</c> delegated ONLY to offer-service's
///     <c>GET /api/v1/jeebers/{id}/offers</c> — a route the deployed offer-service does not
///     expose — so degrade-don't-fail collapsed the list to <c>[]</c>.</item>
/// </list>
///
/// The fix composes the jeeber's own offers (terminal INCLUDED, honest status) from the
/// direct upstream read merged with <see cref="IPendingOffersStore.ListForJeeberAsync"/> —
/// in-memory: full any-status scan; upstream: routing-index + owner-scoped request-list
/// composition. Customer-facing offers surfaces are untouched.
/// </summary>
public class JeeberOfferTerminalVisibilityTests
{
    // -----------------------------------------------------------------
    // In-memory (flag OFF) path
    // -----------------------------------------------------------------

    [Fact]
    public async Task InMemory_LosingJeeber_KeepsSeeingOwnOffer_AsSuperseded()
    {
        using var factory = InMemoryFactory();

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberA = $"jeeber-a-{Guid.NewGuid():N}";
        var jeeberB = $"jeeber-b-{Guid.NewGuid():N}";

        var offerA = await SubmitOfferAsync(factory, jeeberA, requestId, fee: 9m);
        var offerB = await SubmitOfferAsync(factory, jeeberB, requestId, fee: 8m);

        // The customer accepts A — the in-memory auction-close supersedes B's bid.
        var accept = await Client(factory, clientId, "customer")
            .PostAsync($"/v1/offers/{offerA}/accept", null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        // B's flat my-offers surface: the terminal offer is STILL visible, honestly.
        var items = await GetItemsAsync(Client(factory, jeeberB, "driver"),
            $"/v1/offers?jeeberId={jeeberB}");
        items.Should().ContainSingle(o => o.Id == offerB)
            .Which.Status.Should().Be(PendingOfferStatus.Superseded,
                "the losing bid's terminal state must not vanish from the bidder's own list");

        // The bearer-keyed sibling (the exact route run-23 called and 404'd on).
        var mine = await GetItemsAsync(Client(factory, jeeberB, "driver"), "/v1/jeebers/me/offers");
        mine.Should().ContainSingle(o => o.Id == offerB)
            .Which.Status.Should().Be(PendingOfferStatus.Superseded);

        // The winner's own list shows accepted.
        var winners = await GetItemsAsync(Client(factory, jeeberA, "driver"), "/v1/jeebers/me/offers");
        winners.Should().ContainSingle(o => o.Id == offerA)
            .Which.Status.Should().Be(PendingOfferStatus.Accepted);
    }

    [Fact]
    public async Task InMemory_StatusFilter_NarrowsButDefaultIncludesTerminal()
    {
        using var factory = InMemoryFactory();

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = $"jeeber-{Guid.NewGuid():N}";

        var offerId = await SubmitOfferAsync(factory, jeeber, requestId, fee: 7m);

        // Withdraw the bid (terminal, self-retracted).
        var withdraw = await Client(factory, jeeber, "driver").DeleteAsync($"/v1/offers/{offerId}");
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var http = Client(factory, jeeber, "driver");

        // Default: terminal included, honest status.
        var all = await GetItemsAsync(http, $"/v1/offers?jeeberId={jeeber}");
        all.Should().ContainSingle(o => o.Id == offerId)
            .Which.Status.Should().Be(PendingOfferStatus.Withdrawn);

        // ?status= filter narrows.
        var withdrawn = await GetItemsAsync(http, $"/v1/offers?jeeberId={jeeber}&status=withdrawn");
        withdrawn.Should().ContainSingle(o => o.Id == offerId);

        var pending = await GetItemsAsync(http, $"/v1/offers?jeeberId={jeeber}&status=pending");
        pending.Should().BeEmpty("the withdrawn bid is not live");
    }

    // -----------------------------------------------------------------
    // Upstream (flag ON) path
    // -----------------------------------------------------------------

    [Fact]
    public async Task Upstream_LosingJeebersRejectedOffer_IsComposedViaOwnerScopedRead()
    {
        // Models the run-23 production reality: offer-service has NO jeeber-scoped list
        // route (the direct read yields empty), but DOES serve the owner-scoped
        // GET /api/v1/requests/{id}/offers. The gateway must recover B's terminal offer
        // through the submit-time routing index + the owner-scoped request read.
        var fake = new ComposingFakeOfferServiceClient();
        using var factory = UpstreamFactory(fake);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeberA = $"jeeber-a-{Guid.NewGuid():N}";
        var jeeberB = $"jeeber-b-{Guid.NewGuid():N}";
        const string offerA = "up-offer-a";
        const string offerB = "up-offer-b";

        // The pairings the gateway learned at submit time.
        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record(offerA, requestId, jeeberA);
        index.Record(offerB, requestId, jeeberB);

        // offer-service's post-accept truth for the request: A accepted, B rejected.
        fake.RequestOffers[requestId] = new List<OfferWire>
        {
            new() { Id = offerA, RequestId = requestId, JeeberId = jeeberA, Status = "accepted", FeeCents = 900, EtaMinutes = 12, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-9) },
            new() { Id = offerB, RequestId = requestId, JeeberId = jeeberB, Status = "rejected", FeeCents = 850, EtaMinutes = 15, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-8) },
        };

        // B's own list: exactly B's offer, honest terminal status, dollars mapped.
        var items = await GetItemsAsync(Client(factory, jeeberB, "driver"),
            $"/v1/offers?jeeberId={jeeberB}");
        var bOffer = items.Should().ContainSingle().Subject;
        bOffer.Id.Should().Be(offerB);
        bOffer.Status.Should().Be(PendingOfferStatus.Superseded,
            "rejected-by-auction-close maps to the honest 'not selected' state, not a fake 'withdrawn'");
        bOffer.Fee.Should().Be(8.50m, "fee_cents map to dollars");
        items.Should().NotContain(o => o.Id == offerA,
            "a jeeber must never see another bidder's offer through their own list");

        // The upstream read was authorized as the request OWNER (offer-service's contract).
        fake.ListForRequestActingUsers.Should().Contain(clientId);

        // The bearer-keyed sibling route serves the same composition.
        var mine = await GetItemsAsync(Client(factory, jeeberB, "driver"), "/v1/jeebers/me/offers");
        mine.Should().ContainSingle(o => o.Id == offerB)
            .Which.Status.Should().Be(PendingOfferStatus.Superseded);

        // CUSTOMER-facing surface unchanged: the owner still reads B's offer through the
        // legacy three-state fold (rejected → withdrawn), exactly as run-23 observed.
        var customerResp = await Client(factory, clientId, "customer")
            .GetAsync($"/v1/requests/{requestId}/offers");
        customerResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var customerOffers = await customerResp.Content.ReadFromJsonAsync<List<OfferItem>>();
        customerOffers!.Should().ContainSingle(o => o.Id == offerB)
            .Which.Status.Should().Be(PendingOfferStatus.Withdrawn,
                "the customer-facing fold is deliberately untouched by this fix");
    }

    [Fact]
    public async Task Upstream_DirectJeeberRouteWins_WhenItAnswers_NoDuplicates()
    {
        // If offer-service ever grows GET /api/v1/jeebers/{id}/offers, the direct read is
        // authoritative for the rows it serves; the composed store rows must not duplicate.
        var fake = new ComposingFakeOfferServiceClient();
        using var factory = UpstreamFactory(fake);

        var (clientId, requestId) = await SeedRequestAsync(factory);
        var jeeber = $"jeeber-{Guid.NewGuid():N}";
        const string offerId = "up-offer-direct";

        var index = factory.Services.GetRequiredService<IOfferRequestIndex>();
        index.Record(offerId, requestId, jeeber);

        fake.JeeberOffers[jeeber] = new List<JeeberFeedOffer>
        {
            new() { OfferId = offerId, RequestId = requestId, Status = "submitted", FeeCents = 1250, EtaMinutes = 20, CreatedAt = DateTimeOffset.UtcNow },
        };
        fake.RequestOffers[requestId] = new List<OfferWire>
        {
            new() { Id = offerId, RequestId = requestId, JeeberId = jeeber, Status = "submitted", FeeCents = 1250, EtaMinutes = 20, CreatedAt = DateTimeOffset.UtcNow },
        };

        var items = await GetItemsAsync(Client(factory, jeeber, "driver"),
            $"/v1/offers?jeeberId={jeeber}");

        var item = items.Should().ContainSingle("the merge dedupes by offer id").Subject;
        item.Id.Should().Be(offerId);
        item.Status.Should().Be("submitted", "the direct upstream row passes its raw status through");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> InMemoryFactory()
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    // Models the deployed reality even on the flag-off path: the direct
                    // jeeber-scoped upstream route answers nothing (it does not exist),
                    // so the surface must be fed by the store composition.
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton<IOfferServiceClient>(new ComposingFakeOfferServiceClient());
                });
            });

    private static WebApplicationFactory<Program> UpstreamFactory(IOfferServiceClient fake)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["FeatureFlags:UseUpstream:Offer"] = "true" }));
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(fake);
                });
            });

    private static async Task<(string clientId, string requestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "terminal-visibility parcel",
        }, default);
        return (clientId, created.Id);
    }

    private static async Task<string> SubmitOfferAsync(
        WebApplicationFactory<Program> factory, string jeeberId, string requestId, decimal fee)
    {
        var resp = await Client(factory, jeeberId, "driver").PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee, etaMinutes = 20 });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<OfferItem>();
        return dto!.Id;
    }

    private static HttpClient Client(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", role);
        return c;
    }

    private static async Task<List<OfferItem>> GetItemsAsync(HttpClient http, string url)
    {
        var resp = await http.GetAsync(url);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"GET {url} must serve the jeeber's own list");
        var payload = await resp.Content.ReadFromJsonAsync<ItemsEnvelope>();
        return payload!.Items;
    }

    private sealed record ItemsEnvelope(List<OfferItem> Items);

    private sealed record OfferItem(
        string Id, string RequestId, string JeeberId, string Status,
        decimal Fee, int EtaMinutes, string? Note);

    /// <summary>
    /// Fake offer-service: the jeeber-scoped list answers only what
    /// <see cref="JeeberOffers"/> holds (empty by default — the deployed router has no such
    /// route), the owner-scoped request list serves <see cref="RequestOffers"/> and records
    /// the forwarded acting user. Every write/mutation path is unused by these tests.
    /// </summary>
    private sealed class ComposingFakeOfferServiceClient : IOfferServiceClient
    {
        public Dictionary<string, List<JeeberFeedOffer>> JeeberOffers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, List<OfferWire>> RequestOffers { get; } = new(StringComparer.Ordinal);
        public List<string> ListForRequestActingUsers { get; } = new();

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberFeedOffer>>(
                JeeberOffers.TryGetValue(jeeberId, out var offers)
                    ? offers
                    : Array.Empty<JeeberFeedOffer>());

        public Task<IReadOnlyList<OfferWire>> ListForRequestAsync(
            string actingUserId, string requestId, CancellationToken ct)
        {
            ListForRequestActingUsers.Add(actingUserId);
            return Task.FromResult<IReadOnlyList<OfferWire>>(
                RequestOffers.TryGetValue(requestId, out var offers)
                    ? offers
                    : Array.Empty<OfferWire>());
        }

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes, string? note, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents,
            int? etaMinutes, string? note, int? maxEdits, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these tests");
    }
}
