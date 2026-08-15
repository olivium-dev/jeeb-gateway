using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// QA-PRE-JEB-488: the COMMISSION POLICY the gateway still owns (JEB-56).
///
/// <para>gwdbx W2-R11: settlement idempotency and the recorded → batched → paid state machine
/// moved to settlement-service and are its tests now; the end-to-end idempotency claim is pinned
/// by SettlementServiceCutoverW2R11Tests.A2. What stays here is CommissionCalculator, which the
/// gateway keeps for the offers path (WalletSufficiencyGuard) and which must agree, to the cent,
/// with the commission settlement-service computes.</para>
/// </summary>
public class SettlementIdempotencyTests
{
    // ── P2: Commission math ───────────────────────────────────────────────────

    [Theory]
    [InlineData("urgent",      150_000, 0.10, 15_000, 0, 15_000)]   // Express flat 10%
    [InlineData("same-day",    150_000, 0.10, 15_000, 0, 15_000)]   // Standard flat 10%
    [InlineData("economy",     150_000, 0.10, 15_000, 0, 15_000)]   // Standard flat 10%
    [InlineData("on-the-way",  150_000, 0.10, 15_000, 0, 15_000)]   // OnTheWay flat 10%
    [InlineData("unknown",     150_000, 0.10, 15_000, 0, 15_000)]   // Fallback -> Standard
    [InlineData("scheduled",     5_000, 0.10,    500, 0,    500)]   // No floor
    public void CommissionCalculator_MatchesPolicy(
        string tierId, decimal goodsCost,
        decimal expectedRate, decimal expectedCommission,
        decimal expectedInsurance, decimal expectedTotal)
    {
        var tier = CommissionCalculator.ResolveTier(tierId);
        var result = CommissionCalculator.Calculate(goodsCost, tier);

        result.CommissionRate.Should().Be(expectedRate, "rate must match tier policy");
        result.Commission.Should().Be(expectedCommission, "commission must be exact decimal");
        result.Insurance.Should().Be(expectedInsurance, "insurance is not applied");
        result.Total.Should().Be(expectedTotal, "total must equal commission only");
    }

    [Fact]
    public void CommissionCalculator_NoFloatArithmetic_NoMinimumFeeCase()
    {
        // goodsCost=6,666, Standard: 6666 * 0.10 = 666.60, no floor applied.
        var result = CommissionCalculator.Calculate(6_666m, CommissionTier.Standard);

        result.MinimumFeeApplied.Should().BeFalse("there is no minimum commission floor");
        result.Commission.Should().Be(666.60m, "commission is exactly 10% of the accepted offer amount");
        // Verify no floating-point drift: decimal arithmetic only.
        result.Commission.GetType().Should().Be(typeof(decimal));
        result.Total.Should().Be(result.Commission);
    }

    // ── P5: No float arithmetic — decimal types only ─────────────────────────

    [Theory]
    [InlineData(12_345.67)]
    [InlineData(99_999.99)]
    [InlineData(0.01)]
    [InlineData(1_000_000)]
    public void AllCommissionFields_AreDecimal(double goodsCostDouble)
    {
        // Convert from test input to decimal (tests cannot use decimal literals in [InlineData])
        var goodsCost = (decimal)goodsCostDouble;
        var result = CommissionCalculator.Calculate(goodsCost, CommissionTier.Standard);

        // All results must be decimal — verify no lossy float intermediary.
        result.GoodsCost.GetType().Should().Be(typeof(decimal));
        result.Commission.GetType().Should().Be(typeof(decimal));
        result.Insurance.GetType().Should().Be(typeof(decimal));
        result.Total.GetType().Should().Be(typeof(decimal));

        // New money model (Q-001): flat 10% commission, no insurance, no floor — Total == Commission only.
        result.Insurance.Should().Be(0m, "insurance surcharge is retired under Q-001");
        result.Total.Should().Be(result.Commission, "total equals commission only — goods cost and insurance never accumulate into it");
    }
}
