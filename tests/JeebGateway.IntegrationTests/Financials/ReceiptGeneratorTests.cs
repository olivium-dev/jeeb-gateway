using System;
using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// Pure-unit coverage for <see cref="ReceiptGenerator"/> (fix/receipt-autogen).
///
/// The receipt is derived verbatim from the persisted <see cref="Settlement"/>
/// row, so the render must surface the numbers the client and the Jeeber quote
/// in support: the COD cash collected, the flat-10% commission the platform
/// charged, and the Jeeber's payout (COD − commission). No WebApplicationFactory
/// and no Testcontainers — <see cref="ReceiptGenerator.Generate"/> is a pure
/// static, so this fixture is Docker-free and safe to run in a tight loop.
/// </summary>
public class ReceiptGeneratorTests
{
    private static Settlement SettledRow(decimal codAmount, decimal commission) => new()
    {
        Id = "11112222-3333-4444-5555-666677778888",
        DeliveryId = "dlv-1",
        ClientId = "client-1",
        JeeberId = "jeeber-1",
        TierId = "standard",
        GoodsCost = codAmount,
        CommissionTier = CommissionTier.Standard,
        CommissionRate = 0.10m,
        Commission = commission,
        Insurance = 0m,
        Total = commission,
        MinimumFeeApplied = false,
        Currency = "USD",
        PaymentMethod = "cash",
        State = SettlementState.Settled,
        SettledAt = new DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void Generate_SurfacesCodAmount_Commission_Rate_And_Payout()
    {
        // Flat-10% COD loop: $100 collected → $10 commission → $90 Jeeber payout.
        var settlement = SettledRow(codAmount: 100m, commission: 10m);

        var receipt = ReceiptGenerator.Generate(settlement, DateTimeOffset.UtcNow);

        receipt.CodAmount.Should().Be(100m, "the receipt header surfaces the COD cash collected");
        receipt.Commission.Should().Be(10m, "the platform commission is quoted explicitly");
        receipt.CommissionRate.Should().Be(0.10m, "the flat rate is echoed for transparency");
        receipt.Payout.Should().Be(90m, "the Jeeber keeps the COD minus the commission");
        receipt.Total.Should().Be(10m, "Total stays the platform-owed commission (unchanged contract)");
        receipt.SettlementId.Should().Be(settlement.Id);
        receipt.DeliveryId.Should().Be(settlement.DeliveryId);
        receipt.ClientId.Should().Be(settlement.ClientId);
        receipt.JeeberId.Should().Be(settlement.JeeberId);
    }

    [Fact]
    public void Generate_RoundsPayout_ToCents()
    {
        // $12.34 COD → $1.234 commission stored as $1.23 → payout $11.11 (2-dp).
        var settlement = SettledRow(codAmount: 12.34m, commission: 1.23m);

        var receipt = ReceiptGenerator.Generate(settlement, DateTimeOffset.UtcNow);

        receipt.Payout.Should().Be(11.11m, "payout is rounded to whole cents, never a fractional-cent value");
    }
}
