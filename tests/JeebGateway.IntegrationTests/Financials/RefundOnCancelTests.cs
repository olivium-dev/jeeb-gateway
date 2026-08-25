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
using JeebGateway.Financials;
using JeebGateway.Financials.Holds;
using JeebGateway.Financials.Refunds;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using JeebGateway.Requests.Cancellation;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>W5/OD-P1 — one pin per cancellation path in DESIGN §1 (P1/P3/P4/P5/P6): a cancel that
/// lands on a CAPTURED fee credits it back exactly once, on the FROZEN naming of CONTRACT §4.</summary>
/// <remarks>Every test runs with <c>CommissionCollection:Enabled=false</c> — the refund decision is
/// keyed on the LEDGER, never the flag, so a flag flipped off after capture cannot strand the money.</remarks>
public class RefundOnCancelTests
{
    /// <summary>The captured commission the ledger is seeded with (10% of a $100 fee).</summary>
    private const decimal CapturedCommission = 10m;

    /// <summary>CONTRACT §4 FROZEN — deliberately distinct from the capture tag `platform-fee`.</summary>
    private const string RefundTag = "platform-fee-refund";

    // -----------------------------------------------------------------
    // P1 — client cancel pre-pickup (the canonical CancellationService path)
    // -----------------------------------------------------------------

    [Fact]
    public async Task ClientCancelPrePickup_PostCapture_CreditsCapturedFee_Once()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var cancel = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });

        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancel.Content.ReadFromJsonAsync<CancelDeliveryResponse>())!
            .Status.Should().Be(RequestStatus.Cancelled);

        // The credit pairs with the capture under ONE external reference, so the ledger shows
        // debit+credit on the same delivery rather than two unrelated rows.
        WalletCommissionCollector.ExternalReferenceFor(requestId).Should().Be($"delivery:{requestId}");
        await AssertCreditedOnceAsync(engine, requestId, feeWallet);

        // Re-cancelling a terminal row is refused at the store, so the ONE-refund-per-delivery
        // invariant holds without the refunder ever being asked a second time.
        var replay = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        await AssertCreditedOnceAsync(engine, requestId, feeWallet);
    }

    // -----------------------------------------------------------------
    // P4 — jeeber cancel (uniform actor policy: deterrence is the 3+/7d restriction, not the fee)
    // -----------------------------------------------------------------

    [Fact]
    public async Task JeeberCancel_PostCapture_CreditsCapturedFee()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (_, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var cancel = await JeeberActor(factory, jeeberId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { reason = "bike broke down" });

        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await cancel.Content.ReadFromJsonAsync<CancelDeliveryResponse>();
        dto!.Status.Should().Be(RequestStatus.Cancelled);

        await AssertCreditedOnceAsync(engine, requestId, feeWallet);
    }

    // -----------------------------------------------------------------
    // P3 — admin decision on a parked post-pickup client cancel
    // -----------------------------------------------------------------

    [Fact]
    public async Task AdminApproveCancellation_PostCapture_CreditsCapturedFee()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var approvedJeeber = Guid.NewGuid().ToString();
        var approvedWallet = engine.FeeWalletFor(Guid.Parse(approvedJeeber));
        var (approvedClient, approved) = await SeedBoundRowAsync(
            factory, approvedJeeber, RequestStatus.PickedUp);
        engine.SeedExecutedCapture(approved, CapturedCommission, approvedWallet);

        var rejectedJeeber = Guid.NewGuid().ToString();
        var rejectedWallet = engine.FeeWalletFor(Guid.Parse(rejectedJeeber));
        var (rejectedClient, rejected) = await SeedBoundRowAsync(
            factory, rejectedJeeber, RequestStatus.PickedUp);
        engine.SeedExecutedCapture(rejected, CapturedCommission, rejectedWallet);

        // P2 park: post-pickup client cancels are NOT terminal yet, so no money may move here.
        foreach (var (client, delivery) in new[] { (approvedClient, approved), (rejectedClient, rejected) })
        {
            var park = await ClientActor(factory, client)
                .PostAsJsonAsync($"/deliveries/{delivery}/cancel", new { reason = "package wet" });
            park.StatusCode.Should().Be(HttpStatusCode.OK);
            (await park.Content.ReadFromJsonAsync<CancelDeliveryResponse>())!
                .PendingApproval.Should().BeTrue();
        }

        (await RefundCreditsAsync(engine, approved)).Should().BeEmpty("parking is not a terminal cancel");

        var approve = await AdminActor(factory).PatchAsync(
            $"/admin/cancellations/{approved}", JsonContent.Create(new { action = "approve" }));
        approve.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertCreditedOnceAsync(engine, approved, approvedWallet);

        var reject = await AdminActor(factory).PatchAsync(
            $"/admin/cancellations/{rejected}", JsonContent.Create(new { action = "reject", note = "no proof" }));
        reject.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefundCreditsAsync(engine, rejected)).Should().BeEmpty(
            "a rejected cancellation reverts the row to its previous status, so the delivery is still live "
            + "and its fee stays captured");
    }

    // -----------------------------------------------------------------
    // P5 — legacy DELETE /requests/{id} (the least-guarded cancel route)
    // -----------------------------------------------------------------

    [Fact]
    public async Task LegacyDeleteRequest_PostCapture_CreditsCapturedFee()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var cancel = await ClientActor(factory, clientId).DeleteAsync($"/requests/{requestId}");

        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await AssertCreditedOnceAsync(engine, requestId, feeWallet);
    }

    // -----------------------------------------------------------------
    // P6 — bare status PATCH → Cancelled (gated on the COMMITTED upstream verdict)
    // -----------------------------------------------------------------

    [Fact]
    public async Task StatusPatchCancelled_PostCapture_CreditsCapturedFee()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var delivery = new CancellingDeliveryClient();
        await using var factory = NewFactory(engine, refunds, delivery: delivery);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.PickedUp);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var patch = await ClientActor(factory, clientId).PatchAsync(
            $"/deliveries/{requestId}/status", JsonContent.Create(new { to = "Cancelled" }));

        // The refund is gated on the COMMITTED upstream verdict, so the transition must be the
        // thing that happened — not merely the target the caller asked for.
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        delivery.Transitions.Should().ContainSingle();
        delivery.Transitions[0].DeliveryId.Should().Be(requestId);
        delivery.Transitions[0].To.Should().Be(CanonicalDeliveryStatus.Cancelled);

        await AssertCreditedOnceAsync(engine, requestId, feeWallet);
    }

    // -----------------------------------------------------------------
    // Pre-capture — release, not credit (DESIGN §2a: refund-by-construction)
    // -----------------------------------------------------------------

    [Fact]
    public async Task Cancel_PreCapture_ReleasesLiveHold_MovesNoMoney()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var releases = new List<(string RequestId, string Reason)>();
        await using var factory = NewFactory(engine, refunds, releases: releases);

        var jeeberId = Guid.NewGuid().ToString();
        var jeeberGuid = Guid.Parse(jeeberId);
        engine.SetBalance(jeeberGuid, 20m);
        var (clientId, requestId) = await SeedOpenRequestAsync(factory);
        var offerId = await SubmitOfferAsync(factory, jeeberId, requestId);
        engine.HeldTotal(FakeWalletHoldEngine.OfferReference(offerId)).Should().Be(CapturedCommission);

        var cancel = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        // The hold set is ABORTED, on the FROZEN reason — the enum has no per-actor variants.
        releases.Should().Contain(r => r.RequestId == requestId && r.Reason == "request-cancelled");
        engine.PendingHeaders(FakeWalletHoldEngine.OfferReference(offerId)).Should().BeEmpty();
        engine.Headers(FakeWalletHoldEngine.OfferReference(offerId))
            .Should().OnlyContain(h => h.Status == FakeWalletHoldEngine.StatusAborted);

        // Money-neutral both ways: the reservation is back in spendable balance and NOTHING executed,
        // so a pre-capture cancel can never mint a compensating credit for a fee nobody ever took.
        engine.ExecuteCalls.Should().Be(0);
        engine.GrossBalance(jeeberGuid).Should().Be(20m);
        engine.NettedBalance(jeeberGuid).Should().Be(20m);
        (await RefundCreditsAsync(engine, requestId)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Flag-off money-neutrality — the whole seam, end to end
    // -----------------------------------------------------------------

    [Fact]
    public async Task Cancel_FlagOff_NoHold_TouchesNoWallet()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);

        var cancel = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        // Invariant I3, measured on the WHOLE cancel: with CommissionCollection:Enabled=false and
        // an empty ledger, no branch of the release/refund seam may reach a wallet mutation.
        engine.InitiateCalls.Should().Be(0);
        engine.ExecuteCalls.Should().Be(0);
        engine.AbortCalls.Should().Be(0);
        (await RefundCreditsAsync(engine, requestId)).Should().BeEmpty();
    }

    // -----------------------------------------------------------------
    // Fault isolation — the user's cancel never depends on a wallet call
    // -----------------------------------------------------------------

    [Fact]
    public async Task Cancel_RefundFailure_DoesNotFailCancellation()
    {
        var engine = new FakeWalletHoldEngine
        {
            FailNextInitiate = FakeWalletHoldEngine.InitiateFault.ServerError,
        };
        var refunds = new FakeRefundIntentStore();
        await using var factory = NewFactory(engine, refunds);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var cancel = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });

        // The outcome the user sees is computed BEFORE and independently of any wallet call.
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);
        (await cancel.Content.ReadFromJsonAsync<CancelDeliveryResponse>())!
            .Status.Should().Be(RequestStatus.Cancelled);

        (await RefundCreditsAsync(engine, requestId)).Should().BeEmpty("the initiate was refused");

        // The money is still owed, so the durable intent stays OPEN for the sweeper to re-drive —
        // a refund that fails silently is the one failure mode requirement 3 forbids.
        var intent = refunds.Peek(requestId);
        intent.Should().NotBeNull("the intent is written BEFORE any credit is attempted");
        intent!.State.Should().Be(RefundIntentState.Open);
    }

    [Fact]
    public async Task Cancel_PostCapture_RefundRunsDetachedFromTheRequestToken()
    {
        var engine = new FakeWalletHoldEngine();
        var refunds = new FakeRefundIntentStore();
        var tokens = new List<CancellationToken>();
        await using var factory = NewFactory(engine, refunds, refundTokens: tokens);

        var jeeberId = Guid.NewGuid().ToString();
        var feeWallet = engine.FeeWalletFor(Guid.Parse(jeeberId));
        var (clientId, requestId) = await SeedBoundRowAsync(factory, jeeberId, RequestStatus.Accepted);
        engine.SeedExecutedCapture(requestId, CapturedCommission, feeWallet);

        var cancel = await ClientActor(factory, clientId)
            .PostAsJsonAsync($"/deliveries/{requestId}/cancel", new { });
        cancel.StatusCode.Should().Be(HttpStatusCode.OK);

        // W5-F1: the commit already happened, so a client disconnect in the commit→intent-write
        // window must not be able to abandon the credit with no intent and no trace.
        tokens.Should().ContainSingle();
        tokens[0].CanBeCanceled.Should().BeFalse(
            "the post-commit money block must not observe the caller's request token");
        await AssertCreditedOnceAsync(engine, requestId, feeWallet);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    /// <summary>The credit assertions of CONTRACT §4, asserted together: a right-amount credit on
    /// the wrong key, or the right key crediting the wrong wallet, are both silent money bugs.</summary>
    private static async Task AssertCreditedOnceAsync(
        FakeWalletHoldEngine engine, string requestId, Guid feeWalletId)
    {
        var credits = await RefundCreditsAsync(engine, requestId);
        credits.Should().ContainSingle("a cancelled delivery credits its captured fee back exactly once");
        credits[0].IsExecuted.Should().BeTrue("the credit is a two-phase initiate→execute, not a pending header");
        credits[0].Amount.Should().Be(CapturedCommission,
            "the captured commission verbatim — never recomputed from a fee that may have been edited");
        credits[0].SourceWalletId.Should().Be(FakeWalletHoldEngine.SystemWalletId,
            "the capture's legs are read off the ledger and SWAPPED, never re-resolved");
        credits[0].DestinationWalletId.Should().Be(feeWalletId,
            "the money goes back to the wallet the capture debited");

        var headers = engine.Headers(WalletCommissionCollector.ExternalReferenceFor(requestId))
            .Where(h => string.Equals(h.Tag, RefundTag, StringComparison.Ordinal))
            .ToArray();
        headers.Should().ContainSingle();
        headers[0].IdempotencyKey.Should().Be($"refund:{requestId}",
            "the frozen key pairs with the capture's accept:<requestId> and makes a replay converge");
        headers[0].Status.Should().Be(FakeWalletHoldEngine.StatusExecuted);
    }

    private static async Task<FeeLedgerEntry[]> RefundCreditsAsync(
        FakeWalletHoldEngine engine, string requestId)
    {
        var ledger = await engine.ListFeeLedgerByExternalReferenceAsync(
            WalletCommissionCollector.ExternalReferenceFor(requestId), CancellationToken.None);
        return ledger
            .Where(e => string.Equals(e.Tag, RefundTag, StringComparison.Ordinal))
            .ToArray();
    }

    private static WebApplicationFactory<Program> NewFactory(
        FakeWalletHoldEngine engine,
        FakeRefundIntentStore refunds,
        bool commissionEnabled = false,
        IDeliveryServiceClient? delivery = null,
        List<(string RequestId, string Reason)>? releases = null,
        List<CancellationToken>? refundTokens = null)
        => FakeOfferStoreWebApplicationFactory
            .NewWalletGuardFactory(engine, holdsEnabled: true, commissionEnabled: commissionEnabled)
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRefundIntentStore>();
                services.AddSingleton(refunds);
                services.AddSingleton<IRefundIntentStore>(
                    sp => sp.GetRequiredService<FakeRefundIntentStore>());

                if (releases is not null)
                {
                    // Decorator, not a stub: the real manager still runs, so the abort really happens
                    // and only the FROZEN (requestId, reason) pair becomes assertable.
                    services.RemoveAll<IHoldManager>();
                    services.AddSingleton<IHoldManager>(sp => new RecordingHoldManager(
                        ActivatorUtilities.CreateInstance<HoldManager>(sp), releases));
                }

                if (refundTokens is not null)
                {
                    // Decorator, not a stub: the real refunder still credits, and only the token the
                    // seam handed it becomes assertable.
                    services.RemoveAll<IFeeRefunder>();
                    services.AddSingleton<IFeeRefunder>(sp => new TokenRecordingRefunder(
                        ActivatorUtilities.CreateInstance<FeeRefunder>(sp), refundTokens));
                }

                if (delivery is not null)
                {
                    services.RemoveAll<IDeliveryServiceClient>();
                    services.AddSingleton(delivery);
                }
            }));

    /// <summary>A jeeber-bound row parked on <paramref name="targetStatus"/> — the post-accept shape
    /// every capture-bearing cancel path starts from.</summary>
    private static async Task<(string ClientId, string RequestId)> SeedBoundRowAsync(
        WebApplicationFactory<Program> factory, string jeeberId, string targetStatus)
    {
        var (clientId, requestId) = await SeedOpenRequestAsync(factory);
        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();

        var accepted = await store.TryAcceptByJeeberAsync(
            requestId, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: CancellationToken.None);
        accepted.Should().NotBeNull();

        if (!string.Equals(accepted!.Status, targetStatus, StringComparison.Ordinal))
        {
            (await store.SetStatusAsync(requestId, targetStatus, CancellationToken.None))
                .Should().BeTrue();
        }

        return (clientId, requestId);
    }

    private static async Task<(string ClientId, string RequestId)> SeedOpenRequestAsync(
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

    private static async Task<string> SubmitOfferAsync(
        WebApplicationFactory<Program> factory, string jeeberId, string requestId)
    {
        var resp = await JeeberActor(factory, jeeberId).PostAsJsonAsync(
            $"/requests/{requestId}/offers", new { fee = 100m, etaMinutes = 30, note = (string?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<OfferDto>())!.Id;
    }

    private static HttpClient ClientActor(WebApplicationFactory<Program> factory, string clientId)
        => Actor(factory, clientId, Roles.Client);

    private static HttpClient JeeberActor(WebApplicationFactory<Program> factory, string jeeberId)
        => Actor(factory, jeeberId, Roles.Jeeber);

    private static HttpClient AdminActor(WebApplicationFactory<Program> factory)
        => Actor(factory, $"admin-{Guid.NewGuid()}", Roles.Admin);

    private static HttpClient Actor(WebApplicationFactory<Program> factory, string userId, string role)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id", userId);
        http.DefaultRequestHeaders.Add("X-User-Roles", role);
        return http;
    }

    // ── doubles ───────────────────────────────────────────────────────

    /// <summary>Records the token each seam hands the refunder, so "detached from the request"
    /// (W5-F1) is asserted rather than assumed. Behaviour is otherwise the real refunder's.</summary>
    private sealed class TokenRecordingRefunder : IFeeRefunder
    {
        private readonly IFeeRefunder _inner;
        private readonly List<CancellationToken> _tokens;

        public TokenRecordingRefunder(IFeeRefunder inner, List<CancellationToken> tokens)
        {
            _inner = inner;
            _tokens = tokens;
        }

        public Task RefundOnCancelAsync(
            string requestId, string? jeeberId, string cancelledBy, CancellationToken ct)
        {
            lock (_tokens) _tokens.Add(ct);
            return _inner.RefundOnCancelAsync(requestId, jeeberId, cancelledBy, ct);
        }

        public Task<bool> TryRetryAsync(RefundIntent intent, CancellationToken ct)
            => _inner.TryRetryAsync(intent, ct);
    }

    /// <summary>Records the (requestId, reason) of every request-level release without changing
    /// behaviour — the frozen `request-cancelled` reason is otherwise only visible in a log line.</summary>
    private sealed class RecordingHoldManager : IHoldManager
    {
        private readonly IHoldManager _inner;
        private readonly List<(string RequestId, string Reason)> _releases;

        public RecordingHoldManager(IHoldManager inner, List<(string RequestId, string Reason)> releases)
        {
            _inner = inner;
            _releases = releases;
        }

        public Task<HoldPlacement> PlaceOnSubmitAsync(
            Guid jeeberGuid, string jeeberId, string offerId, string requestId,
            decimal thisOfferCommission, CancellationToken ct)
            => _inner.PlaceOnSubmitAsync(jeeberGuid, jeeberId, offerId, requestId, thisOfferCommission, ct);

        public Task<HoldPlacement> RaiseDeltaAsync(
            Guid jeeberGuid, string jeeberId, string offerId, string requestId,
            decimal newFeeCommissionTotal, CancellationToken ct)
            => _inner.RaiseDeltaAsync(jeeberGuid, jeeberId, offerId, requestId, newFeeCommissionTotal, ct);

        public Task ReleaseForOfferAsync(string offerId, string reason, CancellationToken ct)
            => _inner.ReleaseForOfferAsync(offerId, reason, ct);

        public Task ReleaseForRequestAsync(string requestId, string reason, CancellationToken ct)
        {
            lock (_releases) _releases.Add((requestId, reason));
            return _inner.ReleaseForRequestAsync(requestId, reason, ct);
        }

        public Task ReleaseWithdrawnForJeeberAsync(string jeeberId, string reason, CancellationToken ct)
            => _inner.ReleaseWithdrawnForJeeberAsync(jeeberId, reason, ct);

        public Task RollbackLegAsync(string offerId, Guid txId, string reason, CancellationToken ct)
            => _inner.RollbackLegAsync(offerId, txId, reason, ct);

        public Task AbortHoldSetForCaptureAsync(string offerId, CancellationToken ct)
            => _inner.AbortHoldSetForCaptureAsync(offerId, ct);
    }

    /// <summary>Presence double that ALSO commits a canonical transition, so the P6 status-PATCH leg
    /// runs its real success path (the base's transition throws, which would skip the refund block).</summary>
    /// <remarks>Re-lists the interface so this transition REPLACES the base's throwing one; the
    /// evidence-carrying overload's default implementation then delegates here.</remarks>
    private sealed class CancellingDeliveryClient : FakeDeliveryPresenceClient, IDeliveryServiceClient
    {
        public List<(string DeliveryId, string To)> Transitions { get; } = new();

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

        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(
            string deliveryId, string to, string partySource, string actorId, string actorRole,
            CancellationToken ct)
        {
            lock (Transitions) Transitions.Add((deliveryId, to));
            return Task.FromResult(new DeliveryTransitionUpstream
            {
                DeliveryId = deliveryId,
                Status = to,
                TransitionId = Guid.NewGuid().ToString(),
                TransitionedAt = DateTimeOffset.UtcNow,
            });
        }
    }
}
