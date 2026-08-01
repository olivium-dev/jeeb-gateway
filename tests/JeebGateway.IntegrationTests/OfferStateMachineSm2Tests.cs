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
///   <item><b>OFF-04 edit → 503 when the Offer kill-switch is OFF.</b> The gateway
///     is not the offer record-of-truth when <c>FeatureFlags:UseUpstream:Offer ==
///     false</c>, so EDIT short-circuits to <c>503</c> (the cap/owner/accepted rules
///     are owned by offer-service on the flag-ON path). See the "CONTRACT DRIFT —
///     UPDATED (iter5)" note above the edit tests below.</item>
/// </list>
///
/// Build-verify target per the WS-03 work breakdown: the in-memory auction-close
/// authority for ACCEPT, offline-testable. The live Offer :10063 swap (WS-09) owns
/// the edit/reject rules when the flag is on; the flag-OFF edit/reject fallback is
/// 503 (aligned with ADR-0006 in-memory-store retirement).
///
/// Tests share a single WebApplicationFactory (and thus the same in-memory stores);
/// each test scopes itself with unique requestIds / userIds to avoid cross-bleed.
/// </summary>
// GW3 / W3.5(c): the class fixture is now FakeOfferStoreWebApplicationFactory, not a bare
// WebApplicationFactory<Program>. Program.cs used to register an in-memory offer store and
// select it whenever FeatureFlags:UseUpstream:Offer was false, so a bare factory silently
// handed this class a working offer ledger. The gateway ships none now — offer-service is
// the ledger of record — so the fixture supplies the test-owned double explicitly.
public class OfferStateMachineSm2Tests : IClassFixture<Fakes.FakeOfferStoreWebApplicationFactory>
{
    private readonly Fakes.FakeOfferStoreWebApplicationFactory _factory;

    public OfferStateMachineSm2Tests(Fakes.FakeOfferStoreWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // ACC-02 — (Removed 2026-08-01) accept-supersedes and re-accept-409.
    //
    // Four tests lived here: Accept_Supersedes_Other_Pending_Offers_On_Same_Request,
    // Accept_Does_Not_Touch_Offers_On_Other_Requests,
    // ReAccept_Same_Offer_Returns_409_Already_Accepted_With_Winner, and
    // Accept_Of_Superseded_Competing_Offer_Returns_409_Already_Accepted.
    //
    // All four drove POST /offers/{id}/accept, which the owner retired on 2026-08-01
    // as a duplicate of POST /v1/offers/{id}/accept. Specifically they drove that
    // route's flag-OFF LEGACY IN-MEMORY branch — the one that called
    // IPendingOffersStore.AcceptWithSupersedeAsync and rendered
    // "https://jeeb.dev/errors/already-accepted" with a winnerJeeberId extension.
    //
    // BE HONEST ABOUT WHAT WENT WITH IT: the surviving V1 route has NO in-memory
    // branch. It forwards every accept to offer-service, which owns the race-safe
    // single-winner transition AND the sibling supersede, and the gateway re-emits the
    // upstream status verbatim (a re-accept surfaces offer-service's 409
    // "offer-not-pending", NOT the gateway-minted already-accepted/winnerJeeberId
    // shape). That gateway-local supersede rule and its ProblemDetails extension are
    // therefore genuinely gone, not relocated — they were a second implementation of an
    // auction close that offer-service already owns, which is why the surface was
    // retired. The forwarding contract that replaced them is asserted by
    // Gw3Pack/W35c_OfferStoreAndLocalAcceptDeletedTests.C11 and by
    // OfferAcceptColdIndexReconcileTests.Accept_ColdIndex_GenuinelyNonPending_SagaConflict_Returns409.
    //
    // The EDIT tests below are untouched — Edit survives the retirement.
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // OFF-04 — offer EDIT under the default (Offer kill-switch OFF) factory.
    //
    // CONTRACT DRIFT — UPDATED (iter5). These four tests originally asserted a
    // gateway-owned IN-MEMORY edit path (cap→422, owner→403, accepted→409,
    // applies-fields→200) on the flag-OFF fallback. That path was SUPERSEDED:
    // `OffersController.Edit` now short-circuits to 503 when `FeatureFlags:
    // UseUpstream:Offer == false` ("the gateway is not the offer record-of-truth
    // when the kill-switch is off"), aligning EDIT with the existing REJECT rule
    // and the thin-BFF / ADR-0006 (in-memory-store retirement) direction. The
    // in-memory edit rule (the store's own `TryEditAsync`; the `EditInMemoryAsync`
    // controller helper that also named this rule was deleted 2026-08-01 as unreachable)
    // is now unreachable from the HTTP surface.
    //
    // The flag-OFF→503 contract is asserted (and PASSES) by
    // `OfferMutationEndpointTests.A3_Edit_FlagOff_Returns_503` /
    // `A5_Reject_FlagOff_Returns_503`. When the flag is ON the gateway forwards
    // to offer-service, which owns the cap/owner/accepted rules — covered by the
    // `OfferMutationEndpointTests.A3_Edit_*` forwarding tests. So these four are
    // updated to assert the NEW flag-OFF contract (503, upstream never mutated),
    // keeping each scenario meaningful (the auth/identity setup still runs and the
    // in-memory store must stay untouched).
    // -----------------------------------------------------------------

    [Fact]
    public async Task Edit_FlagOff_Returns_503_And_Does_Not_Mutate_Store()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-edit-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 10m, eta: 30);

        var jeeber = JeeberClient(jeeberId);

        // Flag-OFF: the gateway is not the offer record-of-truth → 503, no edit applied.
        var e1 = await jeeber.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 11m });
        e1.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // The bid is untouched — fee still at the submitted value, EditCount unchanged.
        var offers = _factory.Services.GetRequiredService<Fakes.FakePendingOffersStore>();
        var stored = await offers.GetAsync(offerId, default);
        stored!.Fee.Should().Be(10m);
        stored.EditCount.Should().Be(0);
        stored.Status.Should().Be(PendingOfferStatus.Pending);
    }

    [Fact]
    public async Task Edit_FlagOff_With_All_Fields_Still_Returns_503_And_Applies_Nothing()
    {
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-edit2-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 8m, eta: 25);

        var resp = await JeeberClient(jeeberId).PutAsJsonAsync(
            $"/v1/offers/{offerId}", new { fee = 9.5m, etaMinutes = 40, note = "Updated route" });
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        // None of the supplied fields were applied — the store is the submitted bid.
        var offers = _factory.Services.GetRequiredService<Fakes.FakePendingOffersStore>();
        var stored = await offers.GetAsync(offerId, default);
        stored!.Fee.Should().Be(8m);
        stored.EtaMinutes.Should().Be(25);
        stored.EditCount.Should().Be(0);
        stored.Status.Should().Be(PendingOfferStatus.Pending);
    }

    [Fact]
    public async Task Edit_FlagOff_By_Different_Jeeber_Still_Returns_503()
    {
        // Flag-OFF short-circuits to 503 BEFORE any ownership rule runs (the gateway
        // no longer owns the in-memory edit rule); the L2 capability gate (offer.edit.own,
        // keyed {jeeber}) still admits any jeeber-role caller, so this is 503, not 403.
        var clientId = $"client-{Guid.NewGuid()}";
        var ownerJeeber = $"jeeber-owner-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(ownerJeeber, requestId, fee: 7m, eta: 20);

        var intruder = JeeberClient($"jeeber-intruder-{Guid.NewGuid()}");
        var resp = await intruder.PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 99m });
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);

        var offers = _factory.Services.GetRequiredService<Fakes.FakePendingOffersStore>();
        (await offers.GetAsync(offerId, default))!.Fee.Should().Be(7m);
    }

    [Fact]
    public async Task Edit_Empty_Body_Returns_400()
    {
        // The empty-body 400 guard runs BEFORE the flag check, so it is unaffected by
        // the flag-OFF→503 contract drift and still asserts the original behaviour.
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-empty-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 6m, eta: 18);

        var resp = await JeeberClient(jeeberId).PutAsJsonAsync(
            $"/v1/offers/{offerId}", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Edit_FlagOff_Accepted_Offer_Still_Returns_503()
    {
        // Even on an already-accepted (auction-closed) offer, flag-OFF edit is 503 —
        // the not-pending guard lives upstream now, not in the gateway's flag-OFF path.
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-acc-{Guid.NewGuid()}";

        var requestId = await SeedRequestAsync(clientId);
        var offerId = await SubmitOfferViaHttpAsync(jeeberId, requestId, fee: 5m, eta: 15);

        // Drive the offer to ACCEPTED through the store, not over HTTP. This used to
        // POST the retired /offers/{id}/accept route; that route is gone (owner ruling
        // 2026-08-01) and the surviving V1 route forwards to a real offer-service this
        // flag-OFF fixture does not run. The store write reaches the SAME terminal state
        // the assertion below cares about, and keeps this test about EDIT.
        (await _factory.Services.GetRequiredService<Fakes.FakePendingOffersStore>()
            .AcceptWithSupersedeAsync(offerId, DateTimeOffset.UtcNow, CancellationToken.None))
            .Status.Should().Be(AcceptOfferStatus.Accepted);

        var resp = await JeeberClient(jeeberId).PutAsJsonAsync($"/v1/offers/{offerId}", new { fee = 50m });
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
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
        var offers = _factory.Services.GetRequiredService<Fakes.FakePendingOffersStore>();
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
