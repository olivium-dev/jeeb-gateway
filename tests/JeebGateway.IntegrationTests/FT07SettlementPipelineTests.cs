using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// FT-07: verify that the settlement pipeline is wired after a successful
/// handover-OTP verification.
///
/// Key assertions:
///   A1. After OTP verify, a <see cref="SettlementState.PendingSettlement"/> row
///       is created in <see cref="ISettlementStore"/> keyed on deliveryId.
///   A2. A second OTP verify (A7 idempotent replay) does NOT create a second
///       settlement row — the store returns the original placeholder.
///   A3. Calling POST /deliveries/{id}/settle upgrades the pending row to
///       <see cref="SettlementState.Settled"/> (ReplacePendingAsync path).
/// </summary>
public class FT07SettlementPipelineTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public FT07SettlementPipelineTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // ---- A1: pending-settlement row created at OTP verify time ---------------

    [Fact]
    public async Task AfterOtpVerify_PendingSettlementRow_IsCreated()
    {
        // Seed a delivery at heading_off (the legacy OTP gate state).
        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var delivery = await store.TryCreateWithLimitAsync(new CreateRequestInput
        {
            ClientId    = $"client-{Guid.NewGuid()}",
            Description = "Test delivery FT-07",
            TierId      = "standard",
        }, limit: 10, ct: default);

        await store.TryTransitionAsync(delivery.Id, RequestStatus.Matched, null, default);
        // Accept via TryAcceptByJeeberAsync so the OTP is minted onto the row.
        var accepted = await store.TryAcceptByJeeberAsync(
            delivery.Id, $"jeeber-ft07-{Guid.NewGuid()}", int.MaxValue, DateTimeOffset.UtcNow, default);
        var otpCode = accepted!.DeliveryOtp!;
        await store.SetStatusAsync(delivery.Id, RequestStatus.PickedUp,   default);
        await store.SetStatusAsync(delivery.Id, RequestStatus.HeadingOff, default);

        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-User-Id",    delivery.ClientId);
        http.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{delivery.Id}/verify-otp",
            new { OtpCode = otpCode });

        resp.IsSuccessStatusCode.Should().BeTrue(
            $"OTP verify should succeed; got {resp.StatusCode}");

        // The settlement store must now contain a pending_settlement row.
        var settlementStore = _factory.Services.GetRequiredService<ISettlementStore>();
        var row = await settlementStore.GetByDeliveryAsync(delivery.Id, default);

        row.Should().NotBeNull("a pending_settlement row must be enqueued at OTP verify");
        row!.State.Should().Be(SettlementState.PendingSettlement);
        row.DeliveryId.Should().Be(delivery.Id);
    }

    // ---- A2: second OTP verify → same one row (idempotency) ------------------

    [Fact]
    public async Task SameDeliveryId_TwiceInSettlementStore_YieldsOneRow()
    {
        var settlementStore = _factory.Services.GetRequiredService<ISettlementStore>();

        var deliveryId = $"delivery-{Guid.NewGuid()}";

        // Simulate first enqueue.
        var first = BuildPending(deliveryId, id: "row-1");
        var (r1, inserted1) = await settlementStore.TryInsertAsync(first, default);
        inserted1.Should().BeTrue("first insert should succeed");

        // Simulate second enqueue (idempotent replay).
        var second = BuildPending(deliveryId, id: "row-2");
        var (r2, inserted2) = await settlementStore.TryInsertAsync(second, default);
        inserted2.Should().BeFalse("second insert for same deliveryId must be rejected");
        r2.Id.Should().Be("row-1", "existing row is returned verbatim");

        // Verify only one row exists.
        var fetched = await settlementStore.GetByDeliveryAsync(deliveryId, default);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("row-1");
    }

    // ---- A3: ReplacePendingAsync upgrades pending → settled ------------------

    [Fact]
    public async Task ReplacePending_UpgradesPendingToSettled_WithoutDuplicate()
    {
        var settlementStore = _factory.Services.GetRequiredService<ISettlementStore>();
        var deliveryId = $"delivery-{Guid.NewGuid()}";

        // 1. Create placeholder.
        var pending = BuildPending(deliveryId, id: "pending-row");
        await settlementStore.TryInsertAsync(pending, default);

        // 2. Replace with actual settled row.
        var settled = BuildSettled(deliveryId, id: "settled-row");
        var replaced = await settlementStore.ReplacePendingAsync(deliveryId, settled, default);
        replaced.Should().BeTrue("pending row should be replaced");

        // 3. Only the settled row remains.
        var fetched = await settlementStore.GetByDeliveryAsync(deliveryId, default);
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be("settled-row");
        fetched.State.Should().Be(SettlementState.Settled);
        fetched.GoodsCost.Should().Be(50_000m);

        // 4. The old pending id is gone.
        var oldById = await settlementStore.GetByIdAsync("pending-row", default);
        oldById.Should().BeNull("pending placeholder must be removed after replace");
    }

    // ---- helpers -------------------------------------------------------------

    private static Settlement BuildPending(string deliveryId, string id) => new()
    {
        Id              = id,
        DeliveryId      = deliveryId,
        ClientId        = "client-x",
        JeeberId        = "jeeber-x",
        TierId          = "standard",
        GoodsCost       = 0m,
        CommissionTier  = CommissionTier.Standard,
        CommissionRate  = CommissionCalculator.StandardRate,
        Commission      = CommissionCalculator.MinCommissionLbp,
        Insurance       = 0m,
        Total           = CommissionCalculator.MinCommissionLbp,
        MinimumFeeApplied = true,
        Currency        = "LBP",
        PaymentMethod   = "cash",
        State           = SettlementState.PendingSettlement,
        SettledAt       = DateTimeOffset.UtcNow,
    };

    private static Settlement BuildSettled(string deliveryId, string id) => new()
    {
        Id              = id,
        DeliveryId      = deliveryId,
        ClientId        = "client-x",
        JeeberId        = "jeeber-x",
        TierId          = "standard",
        GoodsCost       = 50_000m,
        CommissionTier  = CommissionTier.Standard,
        CommissionRate  = CommissionCalculator.StandardRate,
        Commission      = 7_500m,
        Insurance       = 1_000m,
        Total           = 58_500m,
        MinimumFeeApplied = false,
        Currency        = "LBP",
        PaymentMethod   = "cash",
        State           = SettlementState.Settled,
        SettledAt       = DateTimeOffset.UtcNow,
    };
}
