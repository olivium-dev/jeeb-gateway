namespace JeebGateway.Requests.Cancellation;

/// <summary>
/// JEB-1507: configurable thresholds for the Jeeber progressive-ban
/// cancellation policy. Values are bound from the <c>CancellationPolicy</c>
/// section of appsettings so they can be adjusted per environment without a
/// redeploy.
///
/// Defaults mirror the original hardcoded constants so existing deployments
/// that omit the section preserve the previous behaviour.
/// </summary>
public sealed class CancellationPolicyOptions
{
    public const string SectionName = "CancellationPolicy";

    /// <summary>
    /// Number of cancellations within <c>WeeklyWindowDays</c> that triggers a
    /// Jeeber restriction. Default: 3 (3+/7d rule).
    /// </summary>
    public int WeeklyThreshold { get; init; } = 3;

    /// <summary>
    /// Rolling window in days over which cancellations are counted against
    /// <see cref="WeeklyThreshold"/>. Default: 7.
    /// </summary>
    public int WeeklyWindowDays { get; init; } = 7;

    /// <summary>
    /// Lifetime strike count that triggers a longer or permanent ban
    /// (reserved for future progressive-ban escalation). Default: 5.
    /// </summary>
    public int StrikeThreshold { get; init; } = 5;

    /// <summary>
    /// Duration in hours of the Jeeber restriction applied when
    /// <see cref="WeeklyThreshold"/> is exceeded. Default: 24 hours (one day).
    /// </summary>
    public int RestrictionDurationHours { get; init; } = 24;
}
