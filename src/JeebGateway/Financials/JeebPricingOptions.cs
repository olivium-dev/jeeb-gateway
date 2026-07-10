namespace JeebGateway.Financials;

/// <summary>
/// Configuration section that exposes the Jeeb commission rates as
/// overridable values. The constants in <see cref="CommissionCalculator"/> serve
/// as the defaults; operators can override via the JeebPricing appsettings section
/// without a code change (HG-7 commission-rate decision target).
///
/// Section name: JeebPricing
/// </summary>
public sealed class JeebPricingOptions
{
    public const string SectionName = "JeebPricing";

    /// <summary>Standard tier rate (same-day / scheduled / economy / unknown). Default 10%.</summary>
    public decimal StandardRate { get; set; } = CommissionCalculator.StandardRate;

    /// <summary>Express tier rate (urgent / flash / express). Default 10%.</summary>
    public decimal ExpressRate { get; set; } = CommissionCalculator.ExpressRate;

    /// <summary>On-the-way tier rate. Default 10%.</summary>
    public decimal OnTheWayRate { get; set; } = CommissionCalculator.OnTheWayRate;
}
