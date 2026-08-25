using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Financials;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>T4 (TESTING §5.1, DESIGN §8) — barrier-gated, count-invariant concurrency proof for the
/// c1 admission floor. Contention is Barrier + Task.WhenAll only: no Task.Delay, no sleep, no clock.</summary>
public class WalletGuardConcurrencyTests
{
    private const decimal OfferFee = 100m;
    private const int BurstSize = 10;
    private const int RaceIterations = 50;

    /// <summary>Per-offer commission `c` — the same primitive the guard admits on (10%, AwayFromZero).</summary>
    private static readonly decimal Commission = WalletGuardContract.RequiredCommission(OfferFee);

    // -----------------------------------------------------------------
    // G1 merge gate — two simultaneous offers, a balance that covers ONE.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_TwoSimultaneousOffers_OnlyOneAdmitted_WhenBalanceCoversOne()
    {
        // Holds OFF: Layer A (aggregate) admission is the contract under the rollback switch.
        var engine = new FakeWalletHoldEngine { Balance = Commission };
        await using var factory = NewGatedFactory(engine, holds: false, participants: 2);

        for (var iteration = 0; iteration < RaceIterations; iteration++)
        {
            // A fresh jeeber per iteration: the previous iterations' offers stay in the ledger
            // but belong to other jeebers, so this jeeber's outstanding exposure restarts at 0.
            var jeeberId = Guid.NewGuid().ToString();
            var requestA = await SeedRequestAsync(factory);
            var requestB = await SeedRequestAsync(factory);
            using var jeeber = JeeberClient(factory, jeeberId);

            var results = await Task.WhenAll(
                SubmitAsync(jeeber, requestA),
                SubmitAsync(jeeber, requestB));

            var statuses = string.Join(",", results.Select(r => (int)r.StatusCode));
            results.Count(r => r.StatusCode == HttpStatusCode.Created).Should().Be(1,
                $"iteration {iteration}: a balance covering exactly one offer may admit exactly one "
                + $"(check-then-act admits both) — statuses were [{statuses}]");
            results.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired).Should().Be(1,
                $"iteration {iteration}: the loser is refused on balance, not on any other error "
                + $"— statuses were [{statuses}]");

            foreach (var result in results) result.Dispose();
        }
    }

    // -----------------------------------------------------------------
    // N-parallel burst — interleaving-independent, both admission modes.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_NParallelBurst_AdmitsAtMostFloorBalanceOverCommission()
    {
        // k=1/3 make the floor bite; k=10 (B = N*c) is the control leg that must NOT over-deny.
        foreach (var holds in new[] { false, true })
        {
            foreach (var multiple in new[] { 1, 3, BurstSize })
            {
                await RunBurstAsync(holds, balance: multiple * Commission);
            }
        }
    }

    private static async Task RunBurstAsync(bool holds, decimal balance)
    {
        var engine = new FakeWalletHoldEngine { Balance = balance };
        await using var factory = NewGatedFactory(engine, holds, participants: BurstSize);

        var jeeberId = Guid.NewGuid().ToString();
        using var jeeber = JeeberClient(factory, jeeberId);
        var requestIds = new List<string>();
        for (var i = 0; i < BurstSize; i++) requestIds.Add(await SeedRequestAsync(factory));

        var results = await Task.WhenAll(requestIds.Select(id => SubmitAsync(jeeber, id)));

        var leg = $"holds={holds} balance={balance} commission={Commission}";
        var statuses = string.Join(",", results.Select(r => (int)r.StatusCode));
        var admitted = results.Where(r => r.StatusCode == HttpStatusCode.Created).ToArray();
        var admittedCommission = 0m;
        foreach (var result in admitted)
        {
            var offer = (await result.Content.ReadFromJsonAsync<OfferDto>())!;
            admittedCommission += WalletGuardContract.RequiredCommission(offer.Fee);
        }

        var expected = Math.Min(BurstSize, (int)Math.Floor(balance / Commission));
        admitted.Length.Should().Be(expected,
            $"{leg}: admission is floor(B/c) whatever the interleaving — never WHICH offers won. "
            + "Under holds the netted read already carries the outstanding exposure, so the "
            + "admission must not add it again (DECISION Op 1, last paragraph). "
            + $"Statuses were [{statuses}]");
        admittedCommission.Should().BeLessThanOrEqualTo(balance,
            $"{leg}: the admitted set may never commit more fee liability than the balance covers");
        results.Should().OnlyContain(
            r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.PaymentRequired,
            $"{leg}: a refused bid is a 402 on balance — a burst must not degrade into 5xx. "
            + $"Statuses were [{statuses}]");

        foreach (var result in results.Where(r => r.StatusCode == HttpStatusCode.PaymentRequired))
        {
            var body = JObject.Parse(await result.Content.ReadAsStringAsync());
            body["needed"]!.Value<decimal>().Should().BeGreaterThan(body["available"]!.Value<decimal>(),
                $"{leg}: CONTRACT §2 E1 — a 402 is returned iff needed > available, in both modes");
        }

        foreach (var result in results) result.Dispose();
    }

    // -----------------------------------------------------------------
    // DECISION anchor — the hold placed at submit nets the next submit's read.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Reserve_OnSubmit_DecrementsAvailable_ForNextConcurrentSubmit()
    {
        var engine = new FakeWalletHoldEngine { Balance = Commission };
        await using var factory = FakeOfferStoreWebApplicationFactory.NewWalletGuardFactory(
            engine, holdsEnabled: true);

        var jeeberId = Guid.NewGuid().ToString();
        using var jeeber = JeeberClient(factory, jeeberId);
        var requestA = await SeedRequestAsync(factory);
        var requestB = await SeedRequestAsync(factory);

        // Sequenced, not raced: the reservation is the subject here, and the second submit's
        // verdict must be deterministic. Interleaving is covered by the two burst tests above.
        using var admitted = await SubmitAsync(jeeber, requestA);
        admitted.StatusCode.Should().Be(HttpStatusCode.Created);

        using var refused = await SubmitAsync(jeeber, requestB);
        refused.StatusCode.Should().Be(HttpStatusCode.PaymentRequired,
            "the first offer's pending hold nets the engine balance to zero, so the second bid "
            + "is unbacked even though no money has moved");

        var body = JObject.Parse(await refused.Content.ReadAsStringAsync());
        body["type"]!.Value<string>().Should().Be("https://jeeb.dev/errors/insufficient-wallet-balance");
        body["thisOffer"]!.Value<decimal>().Should().Be(Commission);
        body["outstanding"]!.Value<decimal>().Should().Be(Commission);
        body["needed"]!.Value<decimal>().Should().Be(2 * Commission);
        body["available"]!.Value<decimal>().Should().Be(Commission,
            "CONTRACT §2 E1: under holds `available` is GROSS (pending-netted read + held "
            + "outstanding), so mobile's top-up delta stays needed - available");
        body["currency"]!.Value<string>().Should().Be("USD");
        body["needed"]!.Value<decimal>().Should().BeGreaterThan(body["available"]!.Value<decimal>(),
            "a 402 is returned iff needed > available (CONTRACT §2 E1 invariant)");

        engine.ExecuteCalls.Should().Be(0,
            "money movement stays OFF: a hold is placed and released, never executed, while "
            + "CommissionCollection:Enabled is false");

        var offers = factory.Services.GetRequiredService<FakePendingOffersStore>();
        var onRequestB = await offers.ListForRequestAsync(requestB, CancellationToken.None);
        onRequestB.Where(o => PendingOfferStatus.IsLive(o.Status)).Should().BeEmpty(
            "DECISION Op 1: a refused bid leaves no offer live");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>T2's shared factory + the barrier double. The rendezvous MUST be bounded: the
    /// per-jeeber serializer means the Nth in-flight read never overlaps the first.</summary>
    private static WebApplicationFactory<Program> NewGatedFactory(
        FakeWalletHoldEngine engine, bool holds, int participants)
        => FakeOfferStoreWebApplicationFactory.NewWalletGuardFactory(
            engine, holdsEnabled: holds, walletClient: engine.NewGatedWalletClient(participants));

    private static Task<HttpResponseMessage> SubmitAsync(HttpClient jeeber, string requestId)
        => jeeber.PostAsJsonAsync(
            $"/requests/{requestId}/offers",
            new { fee = OfferFee, etaMinutes = 30, note = (string?)null });

    private static async Task<string> SeedRequestAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            // D2: the offer range guard needs a resolvable tier + pickup point.
            TierId = InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint { Lat = InRangeGeoFixture.Lat, Lng = InRangeGeoFixture.Lng },
            ClientId = $"client-{Guid.NewGuid()}",
            Description = "Pick up a package",
        }, CancellationToken.None);
        return created.Id;
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", jeeberId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }
}
