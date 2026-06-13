using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// FT-07: verify the idempotent settlement-store contract that backs the
/// settlement pipeline.
///
/// Key assertions:
///   A2. A second insert for the same deliveryId (idempotent replay) does NOT
///       create a second settlement row — the store returns the original placeholder.
///   A3. <see cref="ISettlementStore.ReplacePendingAsync"/> upgrades the pending
///       row to <see cref="SettlementState.Settled"/> without duplicating it.
///
/// NOTE (JEB-1479 / #151): the original A1 test drove a delivery through the
/// gateway's linear state machine (TryTransitionAsync) and a SetOtpAsync seam,
/// then POSTed /deliveries/{id}/verify-otp to assert the OTP-verify→settlement
/// enqueue. That linear state-machine path was RETIRED from the gateway by #151
/// (DeliveryStateMachineRetiredGuardTests enforces zero TryTransitionAsync call
/// sites in src), and the OTP-verify→settlement wiring now lives in
/// delivery-service — not the gateway IRequestsStore. The methods A1 depended on
/// (IRequestsStore.TryTransitionAsync, IRequestsStore.SetOtpAsync) no longer exist
/// in source, so A1 cannot be reconciled against the current API and was removed.
/// A2/A3 below exercise the still-canonical ISettlementStore idempotency +
/// ReplacePendingAsync contract directly, with no dependency on the retired path.
/// </summary>
public class FT07SettlementPipelineTests
{
    // ---- A2: second insert for same deliveryId → same one row (idempotency) ---

    [Fact]
    public async Task SameDeliveryId_TwiceInSettlementStore_YieldsOneRow()
    {
        var settlementStore = new InMemorySettlementStore();

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
        var settlementStore = new InMemorySettlementStore();
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
