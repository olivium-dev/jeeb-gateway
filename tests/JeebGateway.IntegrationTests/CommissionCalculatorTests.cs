using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit tests for the pure <see cref="CommissionCalculator"/> (T-backend-016 / JEEB-34).
///
/// Locked policy:
/// <list type="bullet">
///   <item>Standard tier  → 15%</item>
///   <item>Express tier   → 20%</item>
///   <item>OnTheWay tier  → 10%</item>
///   <item>Insurance      → 2% of goodsCost</item>
///   <item>Min commission → 1000 LBP</item>
/// </list>
/// </summary>
public class CommissionCalculatorTests
{
    // Standard 15%, insurance 2%.
    // goodsCost 100,000 LBP → commission 15,000; insurance 2,000; total 117,000.
    [Fact]
    public void Standard_Tier_Applies_15_Percent_Commission_And_2_Percent_Insurance()
    {
        var result = CommissionCalculator.Calculate(100_000m, CommissionTier.Standard);

        result.CommissionRate.Should().Be(0.15m);
        result.Commission.Should().Be(15_000m);
        result.Insurance.Should().Be(2_000m);
        result.Total.Should().Be(117_000m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    [Fact]
    public void Express_Tier_Applies_20_Percent_Commission()
    {
        var result = CommissionCalculator.Calculate(50_000m, CommissionTier.Express);

        result.CommissionRate.Should().Be(0.20m);
        result.Commission.Should().Be(10_000m);
        result.Insurance.Should().Be(1_000m);
        result.Total.Should().Be(61_000m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    [Fact]
    public void OnTheWay_Tier_Applies_10_Percent_Commission()
    {
        var result = CommissionCalculator.Calculate(80_000m, CommissionTier.OnTheWay);

        result.CommissionRate.Should().Be(0.10m);
        result.Commission.Should().Be(8_000m);
        result.Insurance.Should().Be(1_600m);
        result.Total.Should().Be(89_600m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    // 1000 LBP minimum kicks in whenever rate * goodsCost would otherwise
    // produce a smaller commission. Insurance is unaffected.
    [Theory]
    [InlineData(CommissionTier.Standard, 5_000)]   // 15% = 750 → bumped to 1000
    [InlineData(CommissionTier.Express, 4_000)]    // 20% = 800 → bumped to 1000
    [InlineData(CommissionTier.OnTheWay, 9_000)]   // 10% = 900 → bumped to 1000
    public void Minimum_Fee_Is_1000_Lbp(CommissionTier tier, decimal goodsCost)
    {
        var result = CommissionCalculator.Calculate(goodsCost, tier);

        result.MinimumFeeApplied.Should().BeTrue();
        result.Commission.Should().Be(CommissionCalculator.MinCommissionLbp);
        result.Total.Should().Be(goodsCost + 1_000m + result.Insurance);
    }

    // Zero goods cost is a valid edge case (e.g. courier verification flow).
    // Insurance is zero; the 1000 LBP floor still applies because the
    // settlement is a paying transaction.
    [Fact]
    public void Zero_Goods_Cost_Still_Triggers_Minimum_Fee()
    {
        var result = CommissionCalculator.Calculate(0m, CommissionTier.Standard);

        result.GoodsCost.Should().Be(0m);
        result.Commission.Should().Be(1_000m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(1_000m);
        result.MinimumFeeApplied.Should().BeTrue();
    }

    [Fact]
    public void Negative_Goods_Cost_Throws()
    {
        var act = () => CommissionCalculator.Calculate(-1m, CommissionTier.Standard);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData("urgent", CommissionTier.Express)]
    [InlineData("URGENT", CommissionTier.Express)]
    [InlineData("flash", CommissionTier.Express)]
    [InlineData("express", CommissionTier.Express)]
    [InlineData("on-the-way", CommissionTier.OnTheWay)]
    [InlineData("on_the_way", CommissionTier.OnTheWay)]
    [InlineData("ontheway", CommissionTier.OnTheWay)]
    [InlineData("same-day", CommissionTier.Standard)]
    [InlineData("economy", CommissionTier.Standard)]
    [InlineData("scheduled", CommissionTier.Standard)]
    [InlineData(null, CommissionTier.Standard)]
    [InlineData("", CommissionTier.Standard)]
    [InlineData("   ", CommissionTier.Standard)]
    [InlineData("unknown-tier", CommissionTier.Standard)]
    public void ResolveTier_Maps_Tier_Codes_To_Commission_Kind(string? code, CommissionTier expected)
    {
        CommissionCalculator.ResolveTier(code).Should().Be(expected);
    }

    // Fractional input — make sure we round to two decimal places consistently.
    [Fact]
    public void Rounds_To_Two_Decimal_Places_Away_From_Zero()
    {
        var result = CommissionCalculator.Calculate(12_345.67m, CommissionTier.Standard);

        // 15% * 12,345.67 = 1851.8505 → 1851.85
        result.Commission.Should().Be(1_851.85m);
        // 2%  * 12,345.67 = 246.9134  → 246.91
        result.Insurance.Should().Be(246.91m);
        // Total = goods + commission + insurance, rounded.
        result.Total.Should().Be(12_345.67m + 1_851.85m + 246.91m);
    }
}
