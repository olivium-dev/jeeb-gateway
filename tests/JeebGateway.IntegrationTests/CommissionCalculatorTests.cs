using FluentAssertions;
using JeebGateway.Financials;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit tests for the pure <see cref="CommissionCalculator"/> (T-backend-016 / JEEB-34).
///
/// Locked policy:
/// <list type="bullet">
///   <item>Standard tier  → 10%</item>
///   <item>Express tier   → 10%</item>
///   <item>OnTheWay tier  → 10%</item>
///   <item>Insurance      → not applied</item>
///   <item>Min commission → not applied</item>
/// </list>
/// </summary>
public class CommissionCalculatorTests
{
    // Standard 10%, no insurance, no floor.
    // accepted offer 100,000 USD -> commission/total 10,000.
    [Fact]
    public void Standard_Tier_Applies_10_Percent_Commission_Without_Insurance()
    {
        var result = CommissionCalculator.Calculate(100_000m, CommissionTier.Standard);

        result.CommissionRate.Should().Be(0.10m);
        result.Commission.Should().Be(10_000m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(10_000m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    [Fact]
    public void Express_Tier_Applies_10_Percent_Commission()
    {
        var result = CommissionCalculator.Calculate(50_000m, CommissionTier.Express);

        result.CommissionRate.Should().Be(0.10m);
        result.Commission.Should().Be(5_000m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(5_000m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    [Fact]
    public void OnTheWay_Tier_Applies_10_Percent_Commission()
    {
        var result = CommissionCalculator.Calculate(80_000m, CommissionTier.OnTheWay);

        result.CommissionRate.Should().Be(0.10m);
        result.Commission.Should().Be(8_000m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(8_000m);
        result.MinimumFeeApplied.Should().BeFalse();
    }

    [Theory]
    [InlineData(CommissionTier.Standard, 5_000, 500)]
    [InlineData(CommissionTier.Express, 4_000, 400)]
    [InlineData(CommissionTier.OnTheWay, 9_000, 900)]
    public void Minimum_Fee_Is_Not_Applied(CommissionTier tier, decimal goodsCost, decimal expectedCommission)
    {
        var result = CommissionCalculator.Calculate(goodsCost, tier);

        result.MinimumFeeApplied.Should().BeFalse();
        result.Commission.Should().Be(expectedCommission);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(expectedCommission);
    }

    // Zero goods cost is a valid edge case (e.g. courier verification flow).
    [Fact]
    public void Zero_Goods_Cost_Has_Zero_Commission()
    {
        var result = CommissionCalculator.Calculate(0m, CommissionTier.Standard);

        result.GoodsCost.Should().Be(0m);
        result.Commission.Should().Be(0m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(0m);
        result.MinimumFeeApplied.Should().BeFalse();
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

        // 10% * 12,345.67 = 1234.567 -> 1234.57
        result.Commission.Should().Be(1_234.57m);
        result.Insurance.Should().Be(0m);
        result.Total.Should().Be(1_234.57m);
    }
}
