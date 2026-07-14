using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// JEBV4-306 — settlement DURABILITY across a gateway restart / stale replica.
///
/// ROOT CAUSE: <see cref="SettlementService.SettleOnCompletionAsync"/> decided the
/// delivered-status AND the COD amount purely from the volatile
/// <see cref="InMemoryRequestsStore"/>. A gateway restart mid-delivery wipes that row,
/// so a delivery that delivery-service has driven to <c>Done</c> settled
/// <c>NotDelivered</c>/$0 — the jeeber was never credited — and the owner LIST stayed at
/// the stale pre-restart status (AtDoor) while the canonical row read <c>Done</c>.
///
/// THE FIX (this test suite is the regression):
/// <list type="bullet">
///   <item>the delivered-decision derives from the CANONICAL delivery-service state
///         (<see cref="IDeliveryServiceClient.GetCanonicalDeliveryAsync"/>) when the
///         in-memory row cannot answer;</item>
///   <item>the amount derives from the DURABLE pending-settlement snapshot stamped at the
///         AtDoor checkpoint (<see cref="ISettlementService.TrySnapshotPendingCodAsync"/>),
///         which survives the restart;</item>
///   <item>exactly-once + flat-10% are preserved.</item>
/// </list>
///
/// These are in-memory tests: real settlement store + ledger + request store, with the
/// Go delivery-service represented by an in-process double (the same harness convention as
/// <c>JeeberEarningsOnCompleteTests</c>) — no live upstream / no Postgres required.
/// </summary>
public class SettlementDurabilityOnCompleteTests
{
    private const decimal Cod = 100m;               // $100 COD (the canonical E2E example)
    private const decimal ExpectedCommission = 10m; // 100 * 0.10 (flat, no insurance/floor)

    /// <summary>
    /// KEYSTONE: with the request-store row GONE (a restart wiped the in-memory projection),
    /// a delivery-service <c>Done</c> STILL settles the jeeber COD × 10% — the amount is
    /// recovered from the durable pending-settlement snapshot and the delivered-decision from
    /// the canonical state. Before the fix this returned NotDelivered/$0.
    /// </summary>
    [Fact]
    public async Task Done_After_RequestRow_Gone_Still_Settles_Cod_Times_Ten_Percent()
    {
        const string deliveryId = "11111111-1111-4111-8111-111111111111";
        const string clientId = "durable-client";
        const string jeeberId = "durable-jeeber";

        // Shared durable stores that OUTLIVE the process bounce.
        var settlementStore = new InMemorySettlementStore();
        var ledger = new InMemorySettlementLedgerClient(TimeProvider.System);
        var canonical = new StubDeliveryClient
        {
            Canonical = new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                ClientId = clientId,
                JeeberId = jeeberId,
                Status = CanonicalDeliveryStatus.Done,
                TierId = "standard",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        // ---- BEFORE the bounce: the live in-memory row exists (AtDoor + fee), and the
        //      AtDoor checkpoint durably snapshots the COD amount. ----
        var liveRequests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await liveRequests.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = clientId, Description = "parcel" }, default);
        (await liveRequests.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await liveRequests.TrySetAcceptedFeeAsync(created.Id, Cod, default)).Should().BeTrue();
        (await liveRequests.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();

        var preBounce = NewService(settlementStore, liveRequests, ledger, canonical);
        (await preBounce.TrySnapshotPendingCodAsync(deliveryId, default))
            .Should().BeTrue("the AtDoor checkpoint records the durable COD snapshot");

        var snapshot = await settlementStore.GetByDeliveryAsync(deliveryId, default);
        snapshot!.State.Should().Be(SettlementState.PendingSettlement, "the snapshot is a pending placeholder — no credit yet");
        snapshot.LedgerEntryId.Should().BeNullOrEmpty("a pending snapshot never posts a ledger entry");

        // ---- THE BOUNCE: a fresh, EMPTY request store (the in-memory row is gone) sharing
        //      the same durable settlement store + canonical delivery-service. ----
        var afterBounce = NewService(settlementStore, new InMemoryRequestsStore(TimeProvider.System), ledger, canonical);
        (await afterBounce.SettleOnCompletionAsync(deliveryId, default) is var r0 && r0.Outcome == SettlementOutcome.Settled)
            .Should().BeTrue();
        var result = await afterBounce.SettleOnCompletionAsync(deliveryId, default);

        // Idempotent: the second call short-circuits on the now-settled row.
        result.Outcome.Should().Be(SettlementOutcome.AlreadySettled);

        var settled = await settlementStore.GetByDeliveryAsync(deliveryId, default);
        settled!.State.Should().Be(SettlementState.Settled, "the Done completion settles despite the wiped request row");
        settled.JeeberId.Should().Be(jeeberId);
        settled.GoodsCost.Should().Be(Cod, "the COD amount is recovered from the durable snapshot, not the wiped row");
        settled.Commission.Should().Be(ExpectedCommission, "flat 10% preserved: 100 * 0.10");
        settled.Total.Should().Be(ExpectedCommission);
        settled.Insurance.Should().Be(0m);
        settled.LedgerEntryId.Should().NotBeNullOrEmpty("the jeeber is credited exactly once via the wallet ledger");
    }

    /// <summary>
    /// Exactly-once across the bounce: firing completion TWICE (OTP verify then customer
    /// PATCH → Done) after the restart credits the jeeber ONCE — a single settled row, a
    /// single ledger entry.
    /// </summary>
    [Fact]
    public async Task SettleOnCompletion_After_Bounce_Is_Idempotent_No_Double_Credit()
    {
        const string deliveryId = "22222222-2222-4222-8222-222222222222";
        var settlementStore = new InMemorySettlementStore();
        var ledger = new InMemorySettlementLedgerClient(TimeProvider.System);
        var canonical = new StubDeliveryClient
        {
            Canonical = new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                ClientId = "c",
                JeeberId = "j",
                Status = CanonicalDeliveryStatus.Done,
                TierId = "standard",
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        var liveRequests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await liveRequests.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = "c", Description = "parcel" }, default);
        await liveRequests.TryAcceptByJeeberAsync(created.Id, "j", int.MaxValue, DateTimeOffset.UtcNow, default);
        await liveRequests.TrySetAcceptedFeeAsync(created.Id, Cod, default);
        await liveRequests.SetStatusAsync(created.Id, RequestStatus.AtDoor, default);
        await NewService(settlementStore, liveRequests, ledger, canonical).TrySnapshotPendingCodAsync(deliveryId, default);

        var afterBounce = NewService(settlementStore, new InMemoryRequestsStore(TimeProvider.System), ledger, canonical);

        var first = await afterBounce.SettleOnCompletionAsync(deliveryId, default);
        first.Outcome.Should().Be(SettlementOutcome.Settled);
        first.Settlement!.LedgerEntryId.Should().NotBeNullOrEmpty();

        var second = await afterBounce.SettleOnCompletionAsync(deliveryId, default);
        second.Outcome.Should().Be(SettlementOutcome.AlreadySettled);
        second.Settlement!.Id.Should().Be(first.Settlement.Id, "no second settlement row");
        second.Settlement.LedgerEntryId.Should().Be(first.Settlement.LedgerEntryId, "the ledger credit is posted exactly once");
    }

    /// <summary>
    /// Read-through half of JEBV4-306: with NOTHING in the gateway's in-memory projection
    /// (the restart-wiped state), the single-delivery read still surfaces the CANONICAL
    /// <c>Done</c> — not a stale/missing status — so the client's LIST/track view converges
    /// with the settled delivery.
    /// </summary>
    [Fact]
    public async Task Read_Through_Surfaces_Canonical_Done_When_Memory_Is_Empty()
    {
        const string deliveryId = "33333333-3333-4333-8333-333333333333";
        var canonical = new StubDeliveryClient
        {
            Canonical = new DeliveryReadUpstream
            {
                DeliveryId = deliveryId,
                ClientId = "read-client",
                JeeberId = "read-jeeber",
                Status = CanonicalDeliveryStatus.Done,
                CreatedAt = DateTimeOffset.UtcNow
            }
        };

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Delivery", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDeliveryServiceClient>();
                services.AddSingleton<IDeliveryServiceClient>(canonical);
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "read-client");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync($"/v1/deliveries/{deliveryId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString()
            .Should().Be(CanonicalDeliveryStatus.Done,
                "the read-through derives the projected status from the canonical delivery state, not the wiped in-memory row");
    }

    // ----------------------------------------------------------------------

    private static SettlementService NewService(
        ISettlementStore store, IRequestsStore requests, ISettlementLedgerClient wallet, IDeliveryServiceClient delivery)
        => new(store, requests, wallet, delivery, new EarningsCacheInvalidator(), TimeProvider.System, NullLogger<SettlementService>.Instance);

    /// <summary>Delivery-service double: only the canonical single-read is used here; every
    /// other hop is loud so an unexpected call fails the test rather than silently passing.</summary>
    private sealed class StubDeliveryClient : IDeliveryServiceClient
    {
        public DeliveryReadUpstream? Canonical { get; init; }

        public Task<DeliveryReadUpstream?> GetCanonicalDeliveryAsync(string deliveryId, CancellationToken ct)
            => Task.FromResult(Canonical);

        public Task<IReadOnlyList<DeliveryTierDto>> ListTiersAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ShipmentsListDto> ListShipmentsAsync(string? orderId, string? stage, int? limit, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> CreateRequestAsync(CreateDeliveryRequestUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRowUpstream> CreateDeliveryRowAsync(CreateDeliveryRowUpstream body, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryRequestUpstream> GetDeliveryAsync(string deliveryId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryOtpVerifyResult> VerifyOtpAsync(string deliveryId, string otpCode, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryTransitionUpstream> CanonicalTransitionAsync(string deliveryId, string to, string partySource, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverIssueResult> IssueHandoverOtpAsync(string deliveryId, string? codeHash, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryHandoverVerifyResult> VerifyHandoverOtpAsync(string deliveryId, bool success, string actorId, string actorRole, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryCancelResult> CancelDeliveryAsync(string deliveryId, DeliveryCancelUpstreamRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> SetAvailabilityAsync(JeeberAvailabilityUpstreamRequest body, string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream?> GetAvailabilityAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
        public Task<JeeberAvailabilityUpstream> HeartbeatAsync(string jeeberId, double lat, double lng, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeliveryMatchingRunResult> RunMatchingAsync(DeliveryMatchingRunRequest body, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountActiveDeliveriesByJeeberAsync(string jeeberId, CancellationToken ct) => throw new NotSupportedException();
    }
}
