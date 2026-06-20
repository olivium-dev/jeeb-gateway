using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// WS-03 — Offer state machine SM-2 (in-memory path, FeatureFlags:UseUpstream:Offer = false).
///
/// Covers the SM-2 contract (scenario-catalog-mockbackend §SM-2 / Domains 5 &amp; 6):
/// <list type="bullet">
///   <item><b>ACC-02 accept → supersede.</b> Accepting one offer marks every other
///     offer on the SAME request <c>superseded</c> (not <c>withdrawn</c>): the
///     competing bids lost the auction. The winner reads <c>accepted</c>.</item>
///   <item><b>ACC-02 re-accept → 409 already_accepted.</b> Accepting an offer on a
///     request whose auction is already closed returns <c>409</c> with
///     <c>type=already-accepted</c> and surfaces the winning Jeeber id.</item>
///   <item><b>OFF-04 edit (in-memory) + 2-edit cap → 422.</b> A Jeeber may edit
///     their own pending bid up to <c>OfferEditCap</c> (2) times; the 3rd edit is
///     rejected with <c>422 edit_limit_reached</c> and the bid is left intact.</item>
/// </list>
///
/// Build-verify target per the WS-03 work breakdown: the in-memory auction-close
/// authority, offline-testable. The live Offer :10063 swap (WS-09) owns these rules
/// when the flag is on; this hardens the flag-OFF fallback.
///
/// Tests share a single WebApplicationFactory (and thus the same in-memory stores);
/// each test scopes itself with unique requestIds / userIds to avoid cross-bleed.
/// </summary>
public class OfferStateMachineSm2Tests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public OfferStateMachineSm2Tests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // ACC-02 — accept supersedes competing offers on the same request.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Accept_Supersedes_Other_Pending_Offers_On_Same_Request()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var winningJeeber = $"jeeber-win-{Guid.NewGuid()}";
        var losingJeeberA = $"jeeber-loseA-{Guid.NewGuid()}";
        var losingJeeberB = $"jeeber-loseB-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var winning = EnqueueOffer(winningJeeber, requestId);
        var losingA = EnqueueOffer(losingJeeberA, requestId);
        var losingB = EnqueueOffer(losingJeeberB, requestId);

        var resp = await ClientActor(clientId).PostAsync($"/offers/{winning.Id}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        (await offers.GetAsync(winning.Id, default))!.Status.Should().Be(PendingOfferStatus.Accepted);
        // The competing bids are SUPERSEDED — not withdrawn (the Jeebers did not
        // retract them; the auction closed around a different winner).
        (await offers.GetAsync(losingA.Id, default))!.Status.Should().Be(PendingOfferStatus.Superseded);
        (await offers.GetAsync(losingB.Id, default))!.Status.Should().Be(PendingOfferStatus.Superseded);
    }

    [Fact]
    public async Task Accept_Does_Not_Touch_Offers_On_Other_Requests()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var requestId = await SeedRequestAsync(clientId);
        var otherRequestId = await SeedRequestAsync($"client-other-{Guid.NewGuid()}");

        var winning = EnqueueOffer($"jeeber-win-{Guid.NewGuid()}", requestId);
        var unrelated = EnqueueOffer($"jeeber-unrelated-{Guid.NewGuid()}", otherRequestId);

        var resp = await ClientActor(clientId).PostAsync($"/offers/{winning.Id}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        // An offer on an UNRELATED request must stay pending — supersede is
        // request-scoped, never a global sweep.
        (await offers.GetAsync(unrelated.Id, default))!.Status.Should().Be(PendingOfferStatus.Pending);
    }

    // -----------------------------------------------------------------
    // ACC-02 — re-accept → 409 already_accepted (returns the winner).
    // -----------------------------------------------------------------

    [Fact]
    public async Task ReAccept_Same_Offer_Returns_409_Already_Accepted_With_Winner()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var winningJeeber = $"jeeber-win-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var winning = EnqueueOffer(winningJeeber, requestId);

        var client = ClientActor(clientId);
        (await client.PostAsync($"/offers/{winning.Id}/accept", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // Second accept of the SAME (now-accepted) offer.
        var reAccept = await client.PostAsync($"/offers/{winning.Id}/accept", content: null);
        reAccept.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await reAccept.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/already-accepted");
        problem.Detail.Should().Contain(winningJeeber);
        problem.Extensions.Should().ContainKey("winnerJeeberId");
        problem.Extensions["winnerJeeberId"]!.ToString().Should().Contain(winningJeeber);
    }

    [Fact]
    public async Task Accept_Of_Superseded_Competing_Offer_Returns_409_Already_Accepted()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var winningJeeber = $"jeeber-win-{Guid.NewGuid()}";
        var losingJeeber = $"jeeber-lose-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var winning = EnqueueOffer(winningJeeber, requestId);
        var losing = EnqueueOffer(losingJeeber, requestId);

        var client = ClientActor(clientId);
        (await client.PostAsync($"/offers/{winning.Id}/accept", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The losing offer was superseded by the accept; trying to accept IT now
        // must report the auction is already closed and name the actual winner.
        var resp = await client.PostAsync($"/offers/{losing.Id}/accept", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/already-accepted");
        problem.Detail.Should().Contain(winningJeeber);
    }

    // -----------------------------------------------------------------
    // OFF-04 — in-memory edit + 2-edit cap → 422 edit_limit_reached.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Edit_Twice_Succeeds_Then_Third_Edit_Returns_422_EditLimitReached()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-edit-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 10m, eta: 30);

        var jeeber = JeeberClient(jeeberId);

        // Edit #1 — OK.
        var e1 = await jeeber.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 11m });
        e1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Edit #2 — OK (cap is 2).
        var e2 = await jeeber.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 12m });
        e2.StatusCode.Should().Be(HttpStatusCode.OK);

        // Edit #3 — rejected with 422 edit_limit_reached.
        var e3 = await jeeber.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 13m });
        e3.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await e3.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/edit-limit-reached");
        problem.Status.Should().Be((int)HttpStatusCode.UnprocessableEntity);

        // The rejected 3rd edit left the bid at the 2nd-edit value ($12), intact.
        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var stored = await offers.GetAsync(offerId, default);
        stored!.Fee.Should().Be(12m);
        stored.EditCount.Should().Be(2);
    }

    [Fact]
    public async Task Edit_Applies_Supplied_Fields_And_Bumps_Edit_Count()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-edit2-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 8m, eta: 25);

        var resp = await JeeberClient(jeeberId).PutAsJsonAsync(
            $"/v1/offers/{offerId}", new { fee = 9.5m, etaMinutes = 40, note = "Updated route" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var stored = await offers.GetAsync(offerId, default);
        stored!.Fee.Should().Be(9.5m);
        stored.EtaMinutes.Should().Be(40);
        stored.Note.Should().Be("Updated route");
        stored.EditCount.Should().Be(1);
        stored.Status.Should().Be(PendingOfferStatus.Pending);
    }

    [Fact]
    public async Task Edit_By_Different_Jeeber_Returns_403()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var ownerJeeber = $"jeeber-owner-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(ownerJeeber, requestId, fee: 7m, eta: 20);

        var intruder = JeeberClient($"jeeber-intruder-{Guid.NewGuid()}");
        var resp = await intruder.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 99m });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-owned");
    }

    [Fact]
    public async Task Edit_Empty_Body_Returns_400()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-empty-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 6m, eta: 18);

        var resp = await JeeberClient(jeeberId).PutAsJsonAsync(
            $"/v1/offers/{offerId}", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Edit_Accepted_Offer_Returns_409()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-acc-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 5m, eta: 15);

        (await ClientActor(clientId).PostAsync($"/offers/{offerId}/accept", content: null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // The auction is closed; the winning bid can no longer be edited.
        var resp = await JeeberClient(jeeberId).PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 50m });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/offer-not-pending");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    // The acceptor is the request-owning CLIENT (customer role).
    private HttpClient ClientActor(string clientId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", clientId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    // A jeeber-role caller (submits / edits / withdraws bids).
    private HttpClient JeeberClient(string jeeberId)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return c;
    }

    private async Task<string> SeedRequestAsync(string clientId)
    {
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package"
        }, default);
        return created.Id;
    }

    // Seeds an offer directly in the store (used by accept/supersede tests where the
    // routing index is not needed).
    private PendingOffer EnqueueOffer(string jeeberId, string requestId)
    {
        var offers = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        return offers.EnqueueForTest(jeeberId, requestId);
    }

    // Submits a real offer over HTTP so the offer-request routing index is populated
    // (the in-memory edit path resolves offerId → requestId through it).
    private async Task<string> SubmitOfferViaHttpAsync(string jeeberId, string requestId, decimal fee, int eta)
    {
        var resp = await JeeberClient(jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee, etaMinutes = eta });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<OfferDto>();
        return dto!.Id;
    }
}
