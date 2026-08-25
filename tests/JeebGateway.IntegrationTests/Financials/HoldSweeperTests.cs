using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Financials.Holds;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Notifications;
using JeebGateway.service.ServicePushNotification;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>W3/T5 — the hold SWEEPER (DECISION Op 5): the only backstop for a failed release, so
/// these tests are the only proof it repairs rather than guesses at frozen money (R4).</summary>
/// <remarks>Wall-clock-free: <see cref="FakeTimeProvider"/> is the HOST clock and
/// <see cref="HoldSweeper.SweepOnceAsync"/> is driven directly, never the loop (TESTING §1.4).</remarks>
public class HoldSweeperTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Past <c>Holds:OrphanGraceMinutes</c> (15), inside which a terminal transition's
    /// own release may still be in flight.</summary>
    private static readonly TimeSpan PastGrace = TimeSpan.FromMinutes(16);

    // CONTRACT §5 rows P-1 / P-2, EN — literals on purpose: an older mobile build that does not
    // recognise the wire `type` displays these bytes verbatim.
    private const string PushTitle = "Offer withdrawn — top up to keep bidding";

    private const string PushBody =
        "Your winning offer was withdrawn because your wallet no longer covers the 10% platform fee. "
        + "Tap to top up.";

    [Fact]
    public async Task Sweep_ReleasesOrphanHold_AfterGrace()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, intents, clock);

        var jeeberId = Guid.NewGuid().ToString();
        var (offerId, requestId) = await SeedHeldOfferAsync(factory, clock, jeeberId, fee: 100m);
        var reference = FakeWalletHoldEngine.OfferReference(offerId);

        // The leak: the offer goes terminal in the ledger WITHOUT its release hook running.
        await Offers(factory).TryWithdrawAsync(
            offerId, requestId, jeeberId, clock.GetUtcNow(), CancellationToken.None);

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);
        engine.PendingHeaders(reference).Should().ContainSingle(
            "inside the grace window the transition's own release may still be in flight");

        clock.Advance(PastGrace);
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        engine.PendingHeaders(reference).Should().BeEmpty("the orphaned hold is aborted");
        engine.ExecuteCalls.Should().Be(0, "a leak is released, never captured");
        intents.Peek(offerId)!.State.Should().Be(FakeHoldIntentStore.ClosedState);
    }

    [Fact]
    public async Task Sweep_BackfillsMissingHold_ForLiveOffer()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, intents, clock);

        var jeeberId = Guid.NewGuid().ToString();
        var requestId = $"req-{Guid.NewGuid()}";
        var offer = await SeedOfferAsync(factory, clock, jeeberId, requestId, fee: 100m);

        // A live offer whose hold never landed — the oversubscribe race, and every offer that
        // predates the holds rollout.
        intents.Seed(new HoldIntent(
            offer.Id, jeeberId, requestId, 0, 10m, clock.GetUtcNow(), null, HoldIntentState.Open));

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        var reference = FakeWalletHoldEngine.OfferReference(offer.Id);
        engine.HeldTotal(reference).Should().Be(10m, "the shortfall is collateralised, not the offer retracted");
        var placed = engine.PendingHeaders(reference).Should().ContainSingle().Subject;
        placed.Tag.Should().Be("hold");
        placed.IsAdditionalFees.Should().BeFalse();
        placed.IdempotencyKey.Should().Be($"jeeb:hold:{offer.Id}");
    }

    [Fact]
    public async Task Sweep_WithdrawsNewestFirst_AndEmitsInsufficientBalancePush_WhenBackfillInsufficient()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        var push = new RecordingUserPushClient();
        var events = new CapturingGenericEventDispatcher(
            GenericEventDispatchClassification.SkippedDirectDispatchArmed);
        await using var factory = NewFactory(engine, intents, clock, push, events);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 1m);

        // One dollar of spendable balance and two live $10 bids: exactly one can be collateralised.
        var oldRequest = $"req-old-{Guid.NewGuid()}";
        var oldOffer = await SeedOfferAsync(factory, clock, jeeberId, oldRequest, fee: 10m);
        clock.Advance(TimeSpan.FromMinutes(1));
        var newRequest = $"req-new-{Guid.NewGuid()}";
        var newOffer = await SeedOfferAsync(factory, clock, jeeberId, newRequest, fee: 10m);

        await Holds(factory).PlaceOnSubmitAsync(
            jeeberGuid, jeeberId, newOffer.Id, newRequest, 1m, CancellationToken.None);
        intents.Seed(new HoldIntent(
            oldOffer.Id, jeeberId, oldRequest, 0, 1m, clock.GetUtcNow(), null, HoldIntentState.Open));

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        // Newest-first: the freshest bid is the least-committed exposure, so it is the one
        // retracted — and retracting it frees exactly enough to back the older bid.
        (await Offers(factory).GetAsync(newOffer.Id, CancellationToken.None))!
            .Status.Should().Be(PendingOfferStatus.Withdrawn);
        (await Offers(factory).GetAsync(oldOffer.Id, CancellationToken.None))!
            .Status.Should().Be(PendingOfferStatus.Pending);
        engine.HeldTotal(FakeWalletHoldEngine.OfferReference(oldOffer.Id)).Should().Be(1m);
        engine.PendingHeaders(FakeWalletHoldEngine.OfferReference(newOffer.Id)).Should().BeEmpty();

        var send = push.Sends.Should().ContainSingle().Subject;
        send.UserId.Should().Be(jeeberId, "the jeeber whose bid was retracted is the recipient");
        var payload = (IDictionary<string, object?>)send.Payload;
        payload["type"].Should().Be("offer_withdrawn_insufficient_balance");
        payload["type"].Should().NotBe("offer_lost",
            "'not selected' would be a lie: the client never chose, the wallet ran short");
        payload["deepLink"].Should().Be("jeeb://wallet");
        payload["title"].Should().Be(PushTitle);
        payload["body"].Should().Be(PushBody);
        payload["offerId"].Should().Be(newOffer.Id);
        payload["requestId"].Should().Be(newRequest);
        payload["request_id"].Should().Be(newRequest);

        var handover = events.Sent.Should().ContainSingle().Subject;
        handover.EventType.Should().Be("jeeb.offer_withdrawn_insufficient_balance");
        handover.Category.Should().Be("wallet", "the live notification-service route is the wallet one");
        handover.Receiver.Should().Be(jeeberId);
    }

    [Fact]
    public async Task Sweep_ClosesStaleRecord()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, intents, clock);

        var jeeberId = Guid.NewGuid().ToString();
        var offerId = $"offer-{Guid.NewGuid()}";

        // Nothing held, offer over: a failed placement, or a release whose tombstone lost the race.
        intents.Seed(new HoldIntent(
            offerId, jeeberId, $"req-{Guid.NewGuid()}", 0, 10m, clock.GetUtcNow(), null,
            HoldIntentState.Failed));

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        intents.Peek(offerId)!.State.Should().Be(FakeHoldIntentStore.ClosedState);
        engine.AbortCalls.Should().Be(0, "there was never a header to abort");
    }

    [Fact]
    public async Task Sweep_SkipsRecord_WhenOfferEnumerationDegraded()
    {
        var engine = new FakeWalletHoldEngine();
        var intents = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, intents, clock);

        var jeeberId = Guid.NewGuid().ToString();
        var (offerId, requestId) = await SeedHeldOfferAsync(factory, clock, jeeberId, fee: 100m);
        await Offers(factory).TryWithdrawAsync(
            offerId, requestId, jeeberId, clock.GetUtcNow(), CancellationToken.None);
        clock.Advance(PastGrace);

        // OD-C1-3 strict: an unreadable offer ledger makes the offer side UNKNOWN, and both ways
        // of guessing it wrong move real money.
        Offers(factory).ForceListForJeeberDegraded = true;
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        engine.PendingHeaders(FakeWalletHoldEngine.OfferReference(offerId)).Should().ContainSingle();
        engine.AbortCalls.Should().Be(0);
        intents.Peek(offerId)!.State.Should().Be(HoldIntentState.Open);

        // Control: the same record IS repaired once the ledger reads again, so the skip is a
        // deferral and not a silent drop.
        Offers(factory).ForceListForJeeberDegraded = false;
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);
        engine.PendingHeaders(FakeWalletHoldEngine.OfferReference(offerId)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletHoldEngine engine,
        FakeHoldIntentStore intents,
        FakeTimeProvider clock,
        RecordingUserPushClient? push = null,
        CapturingGenericEventDispatcher? events = null)
        => FakeOfferStoreWebApplicationFactory
            .NewWalletGuardFactory(engine, holdsEnabled: true, intentStore: intents, timeProvider: clock)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                if (push is not null)
                {
                    services.RemoveAll<ServicePushNotificationClient>();
                    services.AddSingleton<ServicePushNotificationClient>(push);
                }

                if (events is not null)
                {
                    services.RemoveAll<IGenericEventDispatcher>();
                    services.AddSingleton<IGenericEventDispatcher>(events);
                }
            }));

    /// <summary>The sweeper is hosted (and only when state-service is wired), so tests build it
    /// over the running container instead — same dependencies, the injected fake clock included.</summary>
    private static HoldSweeper Sweeper(WebApplicationFactory<Program> factory)
        => ActivatorUtilities.CreateInstance<HoldSweeper>(factory.Services);

    private static FakePendingOffersStore Offers(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<FakePendingOffersStore>();

    private static IHoldManager Holds(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<IHoldManager>();

    /// <summary>A live offer in the ledger with its hold really placed through the manager, so the
    /// intent record and the wallet header agree exactly as a real submit leaves them.</summary>
    private static async Task<(string OfferId, string RequestId)> SeedHeldOfferAsync(
        WebApplicationFactory<Program> factory, FakeTimeProvider clock, string jeeberId, decimal fee)
    {
        var requestId = $"req-{Guid.NewGuid()}";
        var offer = await SeedOfferAsync(factory, clock, jeeberId, requestId, fee);
        var placement = await Holds(factory).PlaceOnSubmitAsync(
            Guid.Parse(jeeberId), jeeberId, offer.Id, requestId, fee / 10m, CancellationToken.None);
        placement.Placed.Should().BeTrue();
        return (offer.Id, requestId);
    }

    private static Task<PendingOffer> SeedOfferAsync(
        WebApplicationFactory<Program> factory, FakeTimeProvider clock,
        string jeeberId, string requestId, decimal fee)
        => Offers(factory).TrySubmitAsync(
            requestId, jeeberId, fee, etaMinutes: 30, note: null,
            maxPerRequest: int.MaxValue, at: clock.GetUtcNow(), CancellationToken.None);

    private sealed record SendRecord(string UserId, object Payload);

    private sealed class RecordingUserPushClient : ServicePushNotificationClient
    {
        public RecordingUserPushClient() : base("http://localhost", new HttpClient()) { }

        public ConcurrentQueue<SendRecord> Sends { get; } = new();

        public override Task<SentPayloadResponse> Send_notification_to_userAsync(
            string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
        {
            Sends.Enqueue(new SendRecord(user_id, body.Payload));
            return Task.FromResult(new SentPayloadResponse
            {
                Message = "ok",
                Timestamp = DateTimeOffset.UtcNow,
            });
        }
    }
}
