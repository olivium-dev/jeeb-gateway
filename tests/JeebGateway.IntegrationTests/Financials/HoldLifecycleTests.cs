using System;
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
using JeebGateway.Financials;
using JeebGateway.Financials.Holds;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>W3/T5 — the hold LIFECYCLE on the real pipeline (DECISION Ops 1–4): one
/// <see cref="FakeWalletHoldEngine"/> ledger so "held" and "spendable" cannot disagree.</summary>
/// <remarks>Pins what nothing else can: every Op-3 transition releases (a miss is frozen money, R4),
/// and nothing executes while <c>CommissionCollection:Enabled</c> is false (I3).</remarks>
public class HoldLifecycleTests
{
    private const decimal Fee = 100m;

    /// <summary>10% of <see cref="Fee"/> — <c>WalletGuardContract.RequiredCommission</c>.</summary>
    private const decimal Commission = 10m;

    // -----------------------------------------------------------------
    // Op 1 + Op 3 — place on submit, release on withdraw
    // -----------------------------------------------------------------

    [Fact]
    public async Task Release_OnWithdraw_RestoresAvailable()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 20m);
        var (_, requestId) = await SeedRequestAsync(factory);

        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        // The hold moves no money: gross is untouched, spendable is netted down by the hold.
        engine.GrossBalance(jeeberGuid).Should().Be(20m);
        engine.HeldTotal(FakeWalletHoldEngine.OfferReference(offerId)).Should().Be(Commission);
        engine.NettedBalance(jeeberGuid).Should().Be(20m - Commission);

        var withdraw = await JeeberClient(factory, jeeberId)
            .DeleteAsync($"/requests/{requestId}/offers/{offerId}");
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        engine.NettedBalance(jeeberGuid).Should().Be(20m,
            "aborting the hold must put the reserved commission straight back into spendable balance");
        engine.GrossBalance(jeeberGuid).Should().Be(20m, "release is money-neutral");
        engine.ExecuteCalls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Op 3 leak guards — one per trigger in DECISION Op 3 / DESIGN §5.
    // A missed release is frozen jeeber money, so each transition is pinned.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Release_OnWithdraw_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        var withdraw = await JeeberClient(factory, jeeberId)
            .DeleteAsync($"/requests/{requestId}/offers/{offerId}");
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task Release_OnSupersede_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var winnerId = Guid.NewGuid().ToString();
        var loserId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var winnerOffer = await SubmitAsync(factory, winnerId, requestId);
        var loserOffer = await SubmitAsync(factory, loserId, requestId);

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire
            {
                AcceptedOfferId = winnerOffer,
                JeeberId = winnerId,
                RejectedOfferIds = new[] { loserOffer },
            },
        };

        var accept = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{winnerOffer}/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertReleased(engine, intents, loserOffer);
    }

    [Fact]
    public async Task Release_OnExpiry_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var delivery = new ExpiringNearbyDeliveryClient();
        await using var factory = NewFactory(engine, intents, delivery: delivery);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        delivery.Expired = new[]
        {
            new ExpiredDeliveryUpstream
            {
                DeliveryId = requestId,
                ClientId = clientId,
                ExpiredAt = DateTimeOffset.UtcNow,
            },
        };

        await factory.Services.GetRequiredService<RequestExpiryObserver>()
            .ObserveOnceAsync(CancellationToken.None);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task Release_OnDecline_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient
        {
            Reject = new OfferMutationResult { Status = OfferMutationStatus.Ok },
        };
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        var reject = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/reject", content: null);
        reject.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task Release_OnAutoOffline_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var availability = new StubAvailabilityStore();
        await using var factory = NewFactory(engine, intents, availability: availability);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        // The flip really does retract the bids here, which is what earns the release.
        availability.Offers = Offers(factory);
        availability.Online = OnlineButIdle(jeeberId);

        await ActivatorUtilities.CreateInstance<AutoOfflineSweeper>(factory.Services)
            .SweepOnceAsync(CancellationToken.None);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task AutoOffline_KeepsHold_WhenTheWithdrawNeverHappens()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var availability = new StubAvailabilityStore();
        await using var factory = NewFactory(engine, intents, availability: availability);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 20m);
        var (_, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        // PRODUCTION shape (JEBV4-148): offer-service has no bulk withdraw, so the offers stay
        // live and biddable. Releasing on the ATTEMPT would strip live bids of their collateral.
        availability.Offers = null;
        availability.Online = OnlineButIdle(jeeberId);

        await ActivatorUtilities.CreateInstance<AutoOfflineSweeper>(factory.Services)
            .SweepOnceAsync(CancellationToken.None);

        availability.WentOffline.Should().Contain(jeeberId);
        Offers(factory).PeekForTest(jeeberId).Should().ContainSingle("the bid is still live upstream");
        engine.HeldTotal(FakeWalletHoldEngine.OfferReference(offerId)).Should().Be(Commission);
        engine.NettedBalance(jeeberGuid).Should().Be(20m - Commission);
        intents.Peek(offerId)!.State.Should().NotBe(FakeHoldIntentStore.ClosedState);
    }

    [Fact]
    public async Task Release_OnRequestCancelled_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        var cancel = await ClientActor(factory, clientId).DeleteAsync($"/requests/{requestId}");
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task Release_OnV2Cancel_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        // The CANONICAL client cancel (CancellationService), not the legacy DELETE — the route
        // mobile actually calls on an open auction with live bids.
        var cancel = await Actor(factory, clientId, JeebGateway.Users.Roles.Client)
            .PostAsync($"/v1/requests/{requestId}/cancel", content: null);
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        AssertReleased(engine, intents, offerId);
    }

    [Fact]
    public async Task Release_OnAcceptWithCollectionDisabled_LeavesNoPendingHoldOrIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire { AcceptedOfferId = offerId, JeeberId = jeeberId },
        };

        var accept = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        // Nothing will ever capture this hold while collection is off, so the winner's own
        // reservation is released at the auction's close rather than left for the sweeper.
        AssertReleased(engine, intents, offerId);
        engine.ExecuteCalls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Op 4 — capture by conversion (flag ON in-test only)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Capture_OnAccept_ConvertsHoldToDebit()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(
            engine, intents, offerService: offerService, commissionEnabled: true);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 50m);
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire { AcceptedOfferId = offerId, JeeberId = jeeberId },
        };

        var accept = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        // Step 1: the hold set is ABORTED, never executed — a base+deltas set would capture as
        // 1..n headers under the wrong external reference (DECISION Op 4 rejects direct execute).
        var holdReference = FakeWalletHoldEngine.OfferReference(offerId);
        engine.PendingHeaders(holdReference).Should().BeEmpty();
        engine.Headers(holdReference).Should().OnlyContain(
            h => h.Status == FakeWalletHoldEngine.StatusAborted);

        // Step 2: exactly ONE executed debit, on the collector's own key + reference.
        engine.ExecuteCalls.Should().Be(1);
        var debits = engine.Headers($"delivery:{requestId}");
        debits.Should().ContainSingle();
        debits[0].Status.Should().Be(FakeWalletHoldEngine.StatusExecuted);
        debits[0].IdempotencyKey.Should().Be($"accept:{requestId}");
        debits[0].Amount.Should().Be(Commission);
        engine.GrossBalance(jeeberGuid).Should().Be(50m - Commission);
    }

    [Fact]
    public async Task NoExecuteCall_Observable_WhileCollectionDisabled()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 50m);
        var (clientId, acceptedRequest) = await SeedRequestAsync(factory);
        var (_, withdrawnRequest) = await SeedRequestAsync(factory);

        var acceptedOffer = await SubmitAsync(factory, jeeberId, acceptedRequest);
        var withdrawnOffer = await SubmitAsync(factory, jeeberId, withdrawnRequest);
        engine.ExecuteCalls.Should().Be(0, "placing a hold moves no money");

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire { AcceptedOfferId = acceptedOffer, JeeberId = jeeberId },
        };
        var accept = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{acceptedOffer}/accept", content: null);
        accept.StatusCode.Should().Be(HttpStatusCode.OK);

        var withdraw = await JeeberClient(factory, jeeberId)
            .DeleteAsync($"/requests/{withdrawnRequest}/offers/{withdrawnOffer}");
        withdraw.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Invariant I3: with CommissionCollection:Enabled=false NOTHING may reach
        // /Transaction/{id}/execute — holds go live in this epic, money movement does not.
        engine.ExecuteCalls.Should().Be(0);
        engine.GrossBalance(jeeberGuid).Should().Be(50m);
    }

    // -----------------------------------------------------------------
    // Accept-time revalidation (DECISION Op 1 on the accept leg)
    // -----------------------------------------------------------------

    [Fact]
    public async Task AcceptReplay_ThatConflicts_LeavesNoHoldFromTheRevalidation()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 50m);
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire { AcceptedOfferId = offerId, JeeberId = jeeberId },
        };
        var first = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/accept", content: null);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        AssertReleased(engine, intents, offerId);

        // Double-tap on a closed auction: the revalidation re-places (the winner's hold went at
        // accept #1), and NOTHING downstream would ever release it once the saga refuses.
        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Conflict,
            UpstreamCode = "already_accepted",
        };
        var replay = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/accept", content: null);
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);

        AssertReleased(engine, intents, offerId);
        engine.NettedBalance(jeeberGuid).Should().Be(50m, "a refused accept freezes nothing");
        engine.ExecuteCalls.Should().Be(0);
    }

    [Fact]
    public async Task AcceptRevalidation_BackfillsTheShortfall_NotTheWholeCommission()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var offerService = new ScriptedOfferServiceClient();
        await using var factory = NewFactory(engine, intents, offerService: offerService);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 2m * Commission);
        var (clientId, requestId) = await SeedRequestAsync(factory);
        var offerId = await SubmitAsync(factory, jeeberId, requestId);

        // Raise the fee STORE-SIDE so the hold set is genuinely short (the oversubscribe race /
        // failed-raise residue). Held 10 of a required 20, with exactly 10 spendable left.
        await Offers(factory).TryEditAsync(
            offerId, requestId, jeeberId, fee: 2m * Fee, etaMinutes: null, note: null,
            maxEdits: 5, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);

        offerService.Accept = new OfferAcceptResult
        {
            Status = OfferAcceptStatus.Accepted,
            Envelope = new OfferAcceptWire { AcceptedOfferId = offerId, JeeberId = jeeberId },
        };
        var accept = await ClientActor(factory, clientId)
            .PostAsync($"/v1/offers/{offerId}/accept", content: null);

        // Placing the FULL commission would refuse (10 spendable < 20) and wrongly auto-withdraw
        // a winner who can in fact cover what is missing.
        accept.StatusCode.Should().Be(HttpStatusCode.OK);
        engine.Headers(FakeWalletHoldEngine.OfferReference(offerId))
            .Select(h => h.Amount).Should().Equal(Commission, Commission);
        engine.ExecuteCalls.Should().Be(0);
    }

    // -----------------------------------------------------------------
    // Op 1 failure semantics (CONTRACT §2 E1/E5/E6)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Submit_Returns503_AndPlacesNothing_WhenIntentWriteFails()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore { FailNextWrite = true };
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = Fee, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemTypeAsync(resp)).Should().Be("https://jeeb.dev/errors/offer-exposure-unresolvable");

        // Exposure that cannot be TRACKED is exposure that must not be taken: no header was
        // placed and the compensating withdraw left nothing live.
        engine.InitiateCalls.Should().Be(0);
        engine.PendingHeaders().Should().BeEmpty();
        Offers(factory).PeekForTest(jeeberId).Should().BeEmpty();
    }

    [Fact]
    public async Task Submit_402_MapsInitiateInsufficiency()
    {
        var engine = new FakeWalletHoldEngine
        {
            FailNextInitiate = FakeWalletHoldEngine.InitiateFault.InsufficientBalance,
        };
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = Fee, etaMinutes = 30, note = (string?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        (await ProblemTypeAsync(resp)).Should().Be("https://jeeb.dev/errors/insufficient-wallet-balance");
        engine.PendingHeaders().Should().BeEmpty("a refused hold holds nothing");
        Offers(factory).PeekForTest(jeeberId).Should().BeEmpty("no offer minted");
    }

    [Fact]
    public async Task Submit_402_ReReadsTheWallet_ForTheE1Figures()
    {
        var engine = new FakeWalletHoldEngine
        {
            FailNextInitiate = FakeWalletHoldEngine.InitiateFault.InsufficientBalance,
        };
        var intents = new FakeHoldIntentStore();
        var wallet = engine.NewWalletClient();
        await using var factory = NewFactory(engine, intents, walletClient: wallet);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = Fee, etaMinutes = 30, note = (string?)null });

        // CONTRACT §2 E1 needs needed>available, and the PRE-initiate read passed by definition;
        // only a read taken AFTER the refusal can carry honest figures.
        resp.StatusCode.Should().Be(HttpStatusCode.PaymentRequired);
        wallet.Reads.Should().BeGreaterThan(1);
    }

    [Theory]
    [InlineData(FakeWalletHoldEngine.InitiateFault.IdempotencyConflict)]
    [InlineData(FakeWalletHoldEngine.InitiateFault.ServerError)]
    [InlineData(FakeWalletHoldEngine.InitiateFault.Transport)]
    public async Task Submit_503_MapsInitiateConflictOrTransport(FakeWalletHoldEngine.InitiateFault fault)
    {
        var engine = new FakeWalletHoldEngine { FailNextInitiate = fault };
        var intents = new FakeHoldIntentStore();
        await using var factory = NewFactory(engine, intents);

        var jeeberId = Guid.NewGuid().ToString();
        var (_, requestId) = await SeedRequestAsync(factory);

        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = Fee, etaMinutes = 30, note = (string?)null });

        // Everything that is NOT confirmed insufficiency is an outage, never a 402 that would
        // tell a solvent jeeber to top up (CONTRACT §2 E6).
        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await ProblemTypeAsync(resp)).Should().Be("https://jeeb.dev/errors/wallet-service-unavailable");
        engine.PendingHeaders().Should().BeEmpty();
        Offers(factory).PeekForTest(jeeberId).Should().BeEmpty("no offer minted");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>Nothing frozen and nothing left to reconcile: the two halves of a clean Op-3
    /// release, asserted together because either one alone can pass while money stays held.</summary>
    private static void AssertReleased(
        FakeWalletHoldEngine engine, FakeHoldIntentStore intents, string offerId)
    {
        engine.PendingHeaders(FakeWalletHoldEngine.OfferReference(offerId)).Should().BeEmpty(
            $"every header under jeeb:offer:{offerId} must be aborted by the terminal transition");

        var intent = intents.Peek(offerId);
        intent.Should().NotBeNull("the placement wrote a durable intent record");
        intent!.State.Should().Be(FakeHoldIntentStore.ClosedState,
            "a released hold's record is tombstoned, so the sweeper stops reconciling it");
    }

    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletHoldEngine engine,
        FakeHoldIntentStore intents,
        bool holds = true,
        bool commissionEnabled = false,
        IOfferServiceClient? offerService = null,
        IDeliveryServiceClient? delivery = null,
        IAvailabilityStore? availability = null,
        FakeWalletHoldEngine.HoldAwareFakeWalletClient? walletClient = null)
        => FakeOfferStoreWebApplicationFactory
            .NewWalletGuardFactory(
                engine, holds, commissionEnabled: commissionEnabled, intentStore: intents,
                walletClient: walletClient)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                if (offerService is not null)
                {
                    services.RemoveAll<IOfferServiceClient>();
                    services.AddSingleton(offerService);
                }

                if (delivery is not null)
                {
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(delivery);
                }

                if (availability is not null)
                {
                    services.RemoveAll<IAvailabilityStore>();
                    services.AddSingleton(availability);
                    services.RemoveAll<IAutoOfflineNotifier>();
                    services.AddSingleton<IAutoOfflineNotifier>(new InMemoryAutoOfflineNotifier());
                }

                // The expiry observer's push is not under test and its live notifier dials
                // upstream; the in-memory one keeps the release hook the only moving part.
                services.RemoveAll<IRequestExpiryNotifier>();
                services.AddSingleton<IRequestExpiryNotifier>(new InMemoryRequestExpiryNotifier());
            }));

    private static FakePendingOffersStore Offers(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<FakePendingOffersStore>();

    /// <summary>Stale watermark, not a slept clock: the sweep is a pure function of this timestamp.</summary>
    private static IReadOnlyList<JeeberAvailability> OnlineButIdle(string jeeberId)
        => new[]
        {
            new JeeberAvailability
            {
                UserId = jeeberId,
                IsOnline = true,
                LastSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
                LastInteractionAt = DateTimeOffset.UtcNow.AddDays(-1),
            },
        };

    private static async Task<string> SubmitAsync(
        WebApplicationFactory<Program> factory, string jeeberId, string requestId, decimal fee = Fee)
    {
        var resp = await JeeberClient(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee, etaMinutes = 30, note = (string?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<OfferDto>())!.Id;
    }

    private static async Task<string?> ProblemTypeAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null;
    }

    private static async Task<(string ClientId, string RequestId)> SeedRequestAsync(
        WebApplicationFactory<Program> factory)
    {
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var created = await store.CreateAsync(new CreateRequestInput
        {
            TierId = InRangeGeoFixture.TierId,
            PickupLocation = new GeoPoint { Lat = InRangeGeoFixture.Lat, Lng = InRangeGeoFixture.Lng },
            ClientId = clientId,
            Description = "Pick up a package",
        }, CancellationToken.None);
        return (clientId, created.Id);
    }

    private static HttpClient JeeberClient(WebApplicationFactory<Program> factory, string jeeberId)
        => Actor(factory, jeeberId, "driver");

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
        => Actor(factory, clientId, "client");

    private static HttpClient Actor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", userId);
        http.DefaultRequestHeaders.Add("X-User-Roles", role);
        return http;
    }

    // ── doubles ───────────────────────────────────────────────────────

    /// <summary>Offer-service double for the two upstream-only transitions this suite drives
    /// (accept, reject). Every other member is loud so an unexpected hop fails the test.</summary>
    private sealed class ScriptedOfferServiceClient : IOfferServiceClient
    {
        public OfferAcceptResult Accept { get; set; } =
            new() { Status = OfferAcceptStatus.Accepted };

        public OfferMutationResult Reject { get; set; } =
            new() { Status = OfferMutationStatus.Ok };

        public Task<OfferAcceptResult> AcceptWithStatusAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => Task.FromResult(Accept);

        public Task<OfferMutationResult> RejectAsync(
            string actingUserId, string offerId, CancellationToken ct)
            => Task.FromResult(Reject);

        public Task<OfferMutationResult> EditAsync(
            string actingUserId, string requestId, string offerId, long? feeCents, int? etaMinutes,
            string? note, int? maxEdits, CancellationToken ct)
            => Task.FromResult(new OfferMutationResult { Status = OfferMutationStatus.Ok });

        public Task<IReadOnlyList<JeeberFeedOffer>> ListOffersForJeeberAsync(
            string jeeberId, string? status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberFeedOffer>>(Array.Empty<JeeberFeedOffer>());

        public Task<OfferAcceptWire> AcceptAsync(
            string actingUserId, string requestId, string offerId, string idempotencyKey, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<RequestMirrorResult> MirrorRequestAsync(
            string actingUserId, string requestId, string clientId, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWire> SubmitAsync(
            string actingUserId, string requestId, long feeCents, int etaMinutes,
            string? note, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<OfferWithdrawResult> WithdrawAsync(
            string actingUserId, string requestId, string offerId, CancellationToken ct)
            => throw new NotSupportedException();
    }

    /// <summary>The in-range presence double the offer route needs, plus the one expired-delivery
    /// row the expiry observer polls for — both on one client because the gateway resolves one.</summary>
    /// <remarks>Re-lists the interface so the expiry read below REPLACES its default
    /// implementation; without that the base's inherited mapping keeps returning empty.</remarks>
    private sealed class ExpiringNearbyDeliveryClient : FakeDeliveryPresenceClient, IDeliveryServiceClient
    {
        public IReadOnlyList<ExpiredDeliveryUpstream> Expired { get; set; } =
            Array.Empty<ExpiredDeliveryUpstream>();

        public override Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(
            string jeeberId, CancellationToken ct)
            => Task.FromResult<JeeberAvailabilityUpstream?>(new JeeberAvailabilityUpstream
            {
                JeeberId = jeeberId,
                Online = true,
                VehicleType = "car",
                Zone = "downtown",
                Lat = InRangeGeoFixture.Lat,
                Lng = InRangeGeoFixture.Lng,
                LastSeenAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });

        public Task<IReadOnlyList<ExpiredDeliveryUpstream>> ListExpiredDeliveriesAsync(
            DateTimeOffset since, int limit, CancellationToken ct)
            => Task.FromResult(Expired);
    }

    /// <summary>Presence roster for the auto-offline sweep: the online snapshot is arranged, and
    /// the flip is recorded so the test can tell a real transition from a no-op.</summary>
    private sealed class StubAvailabilityStore : IAvailabilityStore
    {
        public IReadOnlyList<JeeberAvailability> Online { get; set; } =
            Array.Empty<JeeberAvailability>();

        /// <summary>Null models the PRODUCTION store, whose bulk withdraw has no upstream route
        /// (JEBV4-148): the jeeber goes offline and every bid stays live.</summary>
        public FakePendingOffersStore? Offers { get; set; }

        public List<string> WentOffline { get; } = new();

        public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
            => Task.FromResult(Online);

        public async Task<GoOfflineResult> GoOfflineAsync(
            string userId, GoOfflineReason reason, CancellationToken ct)
        {
            WentOffline.Add(userId);
            var row = Online.FirstOrDefault(r => string.Equals(r.UserId, userId, StringComparison.Ordinal))
                      ?? new JeeberAvailability { UserId = userId };
            row.IsOnline = false;
            var withdrawn = Offers is null ? 0 : await Offers.WithdrawForJeeberAsync(userId, ct);
            return new GoOfflineResult
            {
                Availability = row,
                WithdrawnOffers = withdrawn,
                WasOnline = true,
            };
        }

        public Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
            => Task.FromResult(
                Online.FirstOrDefault(r => string.Equals(r.UserId, userId, StringComparison.Ordinal))
                ?? new JeeberAvailability { UserId = userId });

        public Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
            => throw new NotSupportedException();

        public Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyList<JeeberAvailability>> ListKnownJeebersAsync(
            DateTimeOffset since, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<JeeberAvailability>>(Array.Empty<JeeberAvailability>());
    }
}
