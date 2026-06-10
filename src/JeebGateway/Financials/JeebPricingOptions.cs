namespace JeebGateway.Financials;

/// <summary>
/// Configuration section that exposes the Jeeb commission rates and floor as
/// overridable values. The constants in <see cref="CommissionCalculator"/> serve
/// as the defaults; operators can override via the JeebPricing appsettings section
/// without a code change (HG-7 commission-rate decision target).
///
/// Section name: JeebPricing
/// </summary>
public sealed class JeebPricingOptions
{
    public const string SectionName = "JeebPricing";

    /// <summary>Standard tier rate (same-day / scheduled / economy / unknown). Default 15%.</summary>
    public decimal StandardRate { get; set; } = CommissionCalculator.StandardRate;

    /// <summary>Express tier rate (urgent / flash / express). Default 20%.</summary>
    public decimal ExpressRate { get; set; } = CommissionCalculator.ExpressRate;

    /// <summary>On-the-way tier rate. Default 10%.</summary>
    public decimal OnTheWayRate { get; set; } = CommissionCalculator.OnTheWayRate;

    /// <summary>Insurance rate applied on all tiers (pass-through cost, not part of Jeeber net). Default 2%.</summary>
    public decimal InsuranceRate { get; set; } = CommissionCalculator.InsuranceRate;

    /// <summary>Minimum commission floor in LBP. Default 1,000 LBP.</summary>
    public decimal MinCommissionLbp { get; set; } = CommissionCalculator.MinCommissionLbp;
}
