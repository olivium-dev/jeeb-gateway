namespace JeebGateway.Financials;

/// <summary>
/// Commission tier feeding <see cref="CommissionCalculator"/>. The gateway
/// stores tiers by code (see <see cref="JeebGateway.Tiers.DeliveryTier"/>),
/// but the commission policy collapses every Jeeb tier into one of three
/// kinds. Mapping lives in <see cref="CommissionCalculator.ResolveTier"/>.
/// </summary>
public enum CommissionTier
{
    /// <summary>10% flat commission. Default for same-day / scheduled / economy.</summary>
    Standard,

    /// <summary>10% flat commission. Faster SLAs (urgent / flash / express).</summary>
    Express,

    /// <summary>10% commission. On-the-way / opportunistic deliveries.</summary>
    OnTheWay
}

/// <summary>
/// Result of a single <see cref="CommissionCalculator.Calculate"/> call.
/// Every monetary field is in the same currency as <see cref="GoodsCost"/>
/// (Jeeb operates in USD) and is rounded to two decimal places.
/// </summary>
public sealed record CommissionBreakdown(
    decimal GoodsCost,
    CommissionTier Tier,
    decimal CommissionRate,
    decimal Commission,
    decimal Insurance,
    decimal Total,
    bool MinimumFeeApplied);

/// <summary>
/// Pure Jeeb-fee calculator (T-backend-016, JEEB-34).
///
/// Total = commission = accepted offer amount * 10%
///
/// Locked-in policy:
/// <list type="bullet">
///   <item>Standard tier → 10%</item>
///   <item>Express tier  → 10%</item>
///   <item>OnTheWay tier → 10%</item>
///   <item>Insurance → not applied</item>
///   <item>Minimum commission → not applied</item>
/// </list>
///
/// No external dependencies — this class is unit-testable in isolation
/// and called from <see cref="SettlementService"/> at settlement time.
/// </summary>
public static class CommissionCalculator
{
    public const decimal FlatRate = 0.10m;

    public const decimal StandardRate = FlatRate;
    public const decimal ExpressRate = FlatRate;
    public const decimal OnTheWayRate = FlatRate;

    public static CommissionBreakdown Calculate(decimal acceptedOfferAmount, CommissionTier tier)
    {
        if (acceptedOfferAmount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(acceptedOfferAmount), "accepted offer amount must be non-negative.");
        }

        var rate = RateFor(tier);
        var commission = Round(acceptedOfferAmount * rate);
        const decimal insurance = 0m;
        var total = commission;

        return new CommissionBreakdown(
            GoodsCost: Round(acceptedOfferAmount),
            Tier: tier,
            CommissionRate: rate,
            Commission: commission,
            Insurance: insurance,
            Total: total,
            MinimumFeeApplied: false);
    }

    /// <summary>
    /// Maps a tier code (DB / catalog identifier) to a <see cref="CommissionTier"/>.
    /// Unknown codes fall back to <see cref="CommissionTier.Standard"/> so a
    /// catalog admin can add a tier without immediately breaking settlement —
    /// the Standard rate is the conservative middle of the three.
    /// </summary>
    public static CommissionTier ResolveTier(string? tierCode)
    {
        if (string.IsNullOrWhiteSpace(tierCode)) return CommissionTier.Standard;

        return tierCode.Trim().ToLowerInvariant() switch
        {
            "urgent" or "flash" or "express" => CommissionTier.Express,
            "on-the-way" or "on_the_way" or "ontheway" => CommissionTier.OnTheWay,
            _ => CommissionTier.Standard
        };
    }

    private static decimal RateFor(CommissionTier tier) => tier switch
    {
        CommissionTier.Standard => StandardRate,
        CommissionTier.Express => ExpressRate,
        CommissionTier.OnTheWay => OnTheWayRate,
        _ => StandardRate
    };

    private static decimal Round(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
