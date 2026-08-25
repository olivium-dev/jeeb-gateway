using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Financials.Holds;
using JeebGateway.Financials.Refunds;
using JeebGateway.IntegrationTests.Fakes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>W5/§4 — the REFUND pass of the sweeper: the only backstop for a compensating credit
/// that never landed, so these tests are the proof it converges instead of double-paying.</summary>
/// <remarks>Wall-clock-free: <see cref="FakeTimeProvider"/> is the HOST clock and
/// <see cref="HoldSweeper.SweepOnceAsync"/> is driven directly, never the loop (TESTING §1.4).</remarks>
public class RefundSweeperTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The captured commission the refund must return VERBATIM — never recomputed
    /// from the fee, so an edited fee cannot drift charge vs refund (DESIGN §2b step 5).</summary>
    private const decimal CapturedFee = 10m;

    // CONTRACT §4 FROZEN bytes — literals on purpose: these are the ledger's public shape and a
    // constant that quietly changed would repoint every refund read.
    private const string CaptureTag = "platform-fee";

    private const string RefundTag = "platform-fee-refund";

    [Fact]
    public async Task Sweep_RetriesOpenRefundIntent_UntilCredited()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var requestId = SeedCapturedFee(engine, jeeberId);
        var reference = WalletCommissionCollector.ExternalReferenceFor(requestId);
        SeedOpenIntent(refunds, clock, requestId, jeeberId, "client");

        // The credit that never landed: wallet-service refuses the initiate, so the pass must
        // leave the record OPEN rather than tombstone a refund that does not exist.
        FailRefundInitiateFor(engine, requestId);
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        Credits(engine, requestId).Should().BeEmpty("a refused initiate moves no money");
        refunds.Peek(requestId)!.State.Should().Be(RefundIntentState.Open);

        engine.OnInitiate = null;
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        var credit = Credits(engine, requestId).Should().ContainSingle().Subject;
        credit.IdempotencyKey.Should().Be($"refund:{requestId}",
            "the frozen key pairs with the capture's accept:<requestId>");
        credit.Amount.Should().Be(CapturedFee, "the captured commission verbatim, never recomputed");
        credit.Status.Should().Be(FakeWalletHoldEngine.StatusExecuted);

        // OD-C3-5: the legs are the capture's, SWAPPED — never re-resolved, so a changed wallet
        // set cannot misroute the credit.
        var ledger = await engine.ListFeeLedgerByExternalReferenceAsync(reference, CancellationToken.None);
        var leg = ledger.Should().ContainSingle(e => e.Tag == RefundTag).Subject;
        leg.SourceWalletId.Should().Be(FakeWalletHoldEngine.SystemWalletId);
        leg.DestinationWalletId.Should().Be(engine.FeeWalletFor(Guid.Parse(jeeberId)));

        refunds.Peek(requestId)!.State.Should().Be(FakeRefundIntentStore.ClosedState);
    }

    [Fact]
    public async Task Sweep_LostCompletion_ReplayConverges_ClosesIntent()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var requestId = SeedCapturedFee(engine, jeeberId);

        // Crashed AFTER the credit executed and BEFORE the tombstone: the refund is real, the
        // record still says open. The ledger pre-check is what stops a second payout.
        engine.SeedExecutedRefund(requestId, CapturedFee, engine.FeeWalletFor(Guid.Parse(jeeberId)));
        SeedOpenIntent(refunds, clock, requestId, jeeberId, "admin");

        var before = Mutations(engine);
        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        Mutations(engine).Should().Be(before, "a replay writes nothing — one refund per delivery");
        Credits(engine, requestId).Should().ContainSingle().Which.Amount.Should().Be(CapturedFee);
        refunds.Peek(requestId)!.State.Should().Be(FakeRefundIntentStore.ClosedState);
    }

    [Fact]
    public async Task Sweep_ConflictIntent_ReportedNotRetried()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var requestId = SeedCapturedFee(engine, jeeberId);

        // Same key, different money: an accounting divergence a blind retry would either paper
        // over or double. It is reported every pass and never re-driven.
        refunds.Seed(new RefundIntent(
            requestId, jeeberId, CapturedFee, "client", clock.GetUtcNow(), null,
            RefundIntentState.Conflict));

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        Mutations(engine).Should().Be((0, 0, 0), "a conflicted refund makes no wallet call");
        Credits(engine, requestId).Should().BeEmpty();
        refunds.Peek(requestId)!.State.Should().Be(RefundIntentState.Conflict);
        refunds.CloseCalls.Should().Be(0, "closing it would erase the divergence instead of reporting it");
    }

    [Fact]
    public async Task Sweep_NoRefundServices_SkipsQuietly()
    {
        var engine = new FakeWalletHoldEngine();
        var holds = new FakeHoldIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds: null, holdIntents: holds);

        var jeeberId = Guid.NewGuid().ToString();
        var offerId = $"offer-{Guid.NewGuid()}";
        holds.Seed(new HoldIntent(
            offerId, jeeberId, $"req-{Guid.NewGuid()}", 0, CapturedFee, clock.GetUtcNow(), null,
            HoldIntentState.Failed));

        // A host without the refund registrations is the rollback shape; the hold reconciler is
        // older and must not lose a pass to the newer pass being absent.
        var sweep = async () => await Sweeper(factory).SweepOnceAsync(CancellationToken.None);
        await sweep.Should().NotThrowAsync();

        holds.Peek(offerId)!.State.Should().Be(FakeHoldIntentStore.ClosedState,
            "the STALE hold branch still runs with no refund services wired");
        Mutations(engine).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task Sweep_HoldEnumerationFails_StillSweepsRefunds()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var holds = new FakeHoldIntentStore { FailEnumeration = true };
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds, holdIntents: holds);

        var jeeberId = Guid.NewGuid().ToString();
        var requestId = SeedCapturedFee(engine, jeeberId);
        SeedOpenIntent(refunds, clock, requestId, jeeberId, "client");

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        // W5-F2: two independent ledgers. A hold prefix-scan outage delays hold repair only — it
        // must never also hold an owed credit hostage until some later pass.
        Credits(engine, requestId).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"refund:{requestId}");
        refunds.Peek(requestId)!.State.Should().Be(FakeRefundIntentStore.ClosedState);
    }

    [Fact]
    public async Task Sweep_OneFaultyRecord_DoesNotStrandOthers()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var clock = new FakeTimeProvider(Start);
        await using var factory = NewFactory(engine, clock, refunds);

        var faultyJeeber = Guid.NewGuid().ToString();
        var healthyJeeber = Guid.NewGuid().ToString();
        var faultyRequest = SeedCapturedFee(engine, faultyJeeber);
        var healthyRequest = SeedCapturedFee(engine, healthyJeeber);
        SeedOpenIntent(refunds, clock, faultyRequest, faultyJeeber, "jeeber");
        SeedOpenIntent(refunds, clock, healthyRequest, healthyJeeber, "jeeber");

        FailRefundInitiateFor(engine, faultyRequest);

        await Sweeper(factory).SweepOnceAsync(CancellationToken.None);

        // Per-record fault isolation: one unrefundable delivery must not hold every other
        // jeeber's money hostage until some later pass.
        Credits(engine, faultyRequest).Should().BeEmpty();
        refunds.Peek(faultyRequest)!.State.Should().Be(RefundIntentState.Open, "it is retried, not dropped");

        Credits(engine, healthyRequest).Should().ContainSingle()
            .Which.IdempotencyKey.Should().Be($"refund:{healthyRequest}");
        refunds.Peek(healthyRequest)!.State.Should().Be(FakeRefundIntentStore.ClosedState);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>The c1 holds host plus the refund doubles; passing <c>refunds: null</c> strips the
    /// refund registrations entirely, which is the "not wired" shape.</summary>
    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletHoldEngine engine,
        FakeTimeProvider clock,
        FakeRefundIntentStore? refunds,
        FakeHoldIntentStore? holdIntents = null)
        => FakeOfferStoreWebApplicationFactory
            .NewWalletGuardFactory(
                engine, holdsEnabled: true, intentStore: holdIntents, timeProvider: clock)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRefundIntentStore>();
                if (refunds is null)
                {
                    services.RemoveAll<IFeeRefunder>();
                    return;
                }

                services.AddSingleton(refunds);
                services.AddSingleton<IRefundIntentStore>(
                    sp => sp.GetRequiredService<FakeRefundIntentStore>());
            }));

    /// <summary>The sweeper is hosted (and only when state-service is wired), so tests build it
    /// over the running container instead — same dependencies, the injected fake clock included.</summary>
    private static HoldSweeper Sweeper(WebApplicationFactory<Program> factory)
        => ActivatorUtilities.CreateInstance<HoldSweeper>(factory.Services);

    /// <summary>An EXECUTED platform-fee capture under <c>delivery:&lt;requestId&gt;</c> — the
    /// post-capture precondition the refund decision is keyed on (the ledger, never the flag).</summary>
    private static string SeedCapturedFee(FakeWalletHoldEngine engine, string jeeberId)
    {
        var requestId = $"req-{Guid.NewGuid()}";
        engine.SeedExecutedCapture(requestId, CapturedFee, engine.FeeWalletFor(Guid.Parse(jeeberId)));
        engine.Headers(WalletCommissionCollector.ExternalReferenceFor(requestId))
            .Should().ContainSingle().Which.Tag.Should().Be(CaptureTag);
        return requestId;
    }

    /// <summary>Refuses the initiate for exactly these requests' refund keys — the engine's fault
    /// script is one-shot and untargeted, which a multi-record pass cannot use.</summary>
    private static void FailRefundInitiateFor(FakeWalletHoldEngine engine, params string[] requestIds)
        => engine.OnInitiate = key =>
        {
            if (requestIds.Any(r => string.Equals(key, FeeRefunder.IdempotencyKeyFor(r), StringComparison.Ordinal)))
            {
                throw new WalletCommissionDebitException(
                    "wallet-service failed to initiate the refund.", HttpStatusCode.InternalServerError);
            }
        };

    private static void SeedOpenIntent(
        FakeRefundIntentStore refunds, FakeTimeProvider clock,
        string requestId, string jeeberId, string cancelledBy)
        => refunds.Seed(new RefundIntent(
            requestId, jeeberId, CapturedFee, cancelledBy, clock.GetUtcNow(), null,
            RefundIntentState.Open));

    /// <summary>Executed refund headers under the delivery reference — the money-moved surface.</summary>
    private static IReadOnlyList<FakeWalletHoldEngine.HoldRecord> Credits(
        FakeWalletHoldEngine engine, string requestId)
        => engine
            .Headers(WalletCommissionCollector.ExternalReferenceFor(requestId))
            .Where(h => string.Equals(h.Tag, RefundTag, StringComparison.Ordinal)
                        && string.Equals(h.Status, FakeWalletHoldEngine.StatusExecuted, StringComparison.Ordinal))
            .ToArray();

    /// <summary>Every mutating wallet call the engine has served; the flag-off and replay pins
    /// assert this tuple is unchanged rather than trusting a log line.</summary>
    private static (int Initiate, int Execute, int Abort) Mutations(FakeWalletHoldEngine engine)
        => (engine.InitiateCalls.Count, engine.ExecuteCalls.Count, engine.AbortCalls.Count);
}
