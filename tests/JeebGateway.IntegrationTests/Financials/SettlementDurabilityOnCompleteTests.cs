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
///   <item>exactly-once + flat-10% are preserved.</item>
/// </list>
///
/// <para><b>gwdbx W2-R11 — a REAL narrowing, pinned below, not papered over.</b> The durable
/// AMOUNT half of the original fix is gone: settlement-service stores money as NULL on a pending
/// intent (there is no "pending with amount" upstream), so an intent can no longer carry the COD
/// figure across a bounce. The outcome is money-SAFE — a bounced delivery with no live row is
/// left UNSETTLED with "no server-authoritative amount yet", never credited $0 — and
/// <see cref="Bounce_Without_Live_Row_Leaves_It_Unsettled_Never_Credits_Zero"/> is the seal.</para>
/// </summary>
public class SettlementDurabilityOnCompleteTests
{
    private const decimal Cod = 100m;               // $100 COD (the canonical E2E example)
    private const decimal ExpectedCommission = 10m; // 100 * 0.10 (flat, no insurance/floor)

    /// <summary>
    /// KEYSTONE: a delivery-service <c>Done</c> settles the jeeber COD × 10% even though the
    /// in-memory request row cannot answer the delivered question — the decision comes from the
    /// canonical state. Before the fix this returned NotDelivered/$0.
    /// </summary>
    [Fact]
    public async Task Canonical_Done_Settles_Cod_Times_Ten_Percent()
    {
        const string deliveryId = "11111111-1111-4111-8111-111111111111";
        const string clientId = "44444444-4444-4444-8444-444444444444";
        const string jeeberId = "55555555-5555-4555-8555-555555555555";

        var settlements = new FakeSettlementServiceClient();
        var canonical = Canonical(deliveryId, clientId, jeeberId);

        // The live row is present but STALE at AtDoor: only the canonical read says Done.
        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await requests.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = clientId, Description = "parcel" }, default);
        (await requests.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default))
            .Should().NotBeNull();
        (await requests.TrySetAcceptedFeeAsync(created.Id, Cod, default)).Should().BeTrue();
        (await requests.SetStatusAsync(created.Id, RequestStatus.AtDoor, default)).Should().BeTrue();

        var service = NewService(settlements, requests, canonical);
        (await service.TrySnapshotPendingCodAsync(deliveryId, default))
            .Should().BeTrue("the AtDoor checkpoint opens the commission window");

        var snapshot = await settlements.GetByDeliveryAsync(deliveryId, default);
        snapshot!.State.Should().Be(SettlementState.PendingSettlement, "an intent is not a credit");
        snapshot.GoodsCost.Should().Be(0m, "W2-R11: an upstream pending intent carries no money");

        (await service.SettleOnCompletionAsync(deliveryId, default)).Outcome
            .Should().Be(SettlementOutcome.Settled);

        var settled = await settlements.GetByDeliveryAsync(deliveryId, default);
        settled!.State.Should().Be(SettlementState.Settled,
            "the canonical Done settles despite the stale in-memory status");
        settled.JeeberId.Should().Be(jeeberId);
        settled.GoodsCost.Should().Be(Cod);
        settled.Commission.Should().Be(ExpectedCommission, "flat 10% preserved: 100 * 0.10");
        settled.Total.Should().Be(ExpectedCommission);
        settled.Insurance.Should().Be(0m);
    }

    /// <summary>
    /// gwdbx W2-R11 NARROWING, sealed: after a bounce that wiped the live row, an amount-less
    /// intent cannot resurrect the COD figure. The delivery is left UNSETTLED with a stated
    /// reason — the one thing that must never happen is a settled row crediting $0.
    /// </summary>
    [Fact]
    public async Task Bounce_Without_Live_Row_Leaves_It_Unsettled_Never_Credits_Zero()
    {
        const string deliveryId = "66666666-6666-4666-8666-666666666666";
        const string clientId = "77777777-7777-4777-8777-777777777777";
        const string jeeberId = "88888888-8888-4888-8888-888888888888";

        var settlements = new FakeSettlementServiceClient();
        var canonical = Canonical(deliveryId, clientId, jeeberId);

        var live = new InMemoryRequestsStore(TimeProvider.System);
        var created = await live.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = clientId, Description = "parcel" }, default);
        await live.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default);
        await live.TrySetAcceptedFeeAsync(created.Id, Cod, default);
        await live.SetStatusAsync(created.Id, RequestStatus.AtDoor, default);
        await NewService(settlements, live, canonical).TrySnapshotPendingCodAsync(deliveryId, default);

        // THE BOUNCE: a fresh, EMPTY request store; only the amount-less intent survives.
        var afterBounce = NewService(settlements, new InMemoryRequestsStore(TimeProvider.System), canonical);
        var result = await afterBounce.SettleOnCompletionAsync(deliveryId, default);

        result.Outcome.Should().Be(SettlementOutcome.AlreadySettled);
        result.Reason.Should().Contain("no server-authoritative amount");

        var row = await settlements.GetByDeliveryAsync(deliveryId, default);
        row!.State.Should().Be(SettlementState.PendingSettlement,
            "money-safe: an unrecoverable amount leaves the window open, it does not credit zero");
        row.GoodsCost.Should().Be(0m);
        row.Commission.Should().Be(0m);
    }

    /// <summary>
    /// Exactly-once: firing completion TWICE (OTP verify then customer PATCH → Done) credits
    /// the jeeber ONCE — a single settled row with a single id.
    /// </summary>
    [Fact]
    public async Task SettleOnCompletion_Is_Idempotent_No_Double_Credit()
    {
        const string deliveryId = "22222222-2222-4222-8222-222222222222";
        const string clientId = "99999999-9999-4999-8999-999999999999";
        const string jeeberId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";

        var settlements = new FakeSettlementServiceClient();
        var canonical = Canonical(deliveryId, clientId, jeeberId);

        var requests = new InMemoryRequestsStore(TimeProvider.System);
        var created = await requests.CreateAsync(
            new CreateRequestInput { Id = deliveryId, ClientId = clientId, Description = "parcel" }, default);
        await requests.TryAcceptByJeeberAsync(created.Id, jeeberId, int.MaxValue, DateTimeOffset.UtcNow, default);
        await requests.TrySetAcceptedFeeAsync(created.Id, Cod, default);
        await requests.SetStatusAsync(created.Id, RequestStatus.AtDoor, default);

        var service = NewService(settlements, requests, canonical);
        var first = await service.SettleOnCompletionAsync(deliveryId, default);
        first.Outcome.Should().Be(SettlementOutcome.Settled);

        var second = await service.SettleOnCompletionAsync(deliveryId, default);
        second.Outcome.Should().Be(SettlementOutcome.AlreadySettled);
        second.Settlement!.Id.Should().Be(first.Settlement!.Id, "no second settlement row");
        settlements.Rows.Should().ContainSingle();
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
        ISettlementServiceClient settlements, IRequestsStore requests, IDeliveryServiceClient delivery)
        => new(settlements, requests, delivery, new EarningsCacheInvalidator(),
            NullLogger<SettlementService>.Instance);

    private static StubDeliveryClient Canonical(string deliveryId, string clientId, string jeeberId)
        => new()
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

    /// <summary>Delivery-service double: only the canonical single-read is used here; every
    /// other hop is loud so an unexpected call fails the test rather than silently passing.</summary>
    private sealed class StubDeliveryClient : IDeliveryServiceClient
    {
    // OA-21 (51a2677) added the provider-audience reads to IDeliveryServiceClient. This double's
    // subject is elsewhere; an empty audience is the neutral answer, not a simulated fault.
    public Task<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>> ListAvailableProvidersAsync(
        double? lat, double? lng, double? radiusKm,
        IReadOnlyCollection<string>? roles, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.AvailableProviderUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.AvailableProviderUpstream>());

    public Task<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>> ListKnownProvidersAsync(
        System.DateTimeOffset since, int limit, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>>(
            System.Array.Empty<JeebGateway.Services.Clients.JeeberAvailabilityUpstream>());

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
