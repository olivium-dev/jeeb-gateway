namespace JeebGateway.Requests;

/// <summary>
/// Tunables for the scheduled-delivery activator (T-backend-046, Phase 2).
///
/// A scheduled request lives in <see cref="RequestStatus.Scheduled"/> from
/// creation until <c>ScheduledAt - MatchingBuffer</c>, at which point the
/// activator flips it to <see cref="RequestStatus.Pending"/> (kicking off
/// matching) and fires reminder notifications to the Client and to every
/// already-matched Jeeber (the matched-Jeeber set is populated by
/// notification preferences / location services upstream of the
/// notification-service; for the MVP we only fan out to the Client).
/// </summary>
public class ScheduledDeliveryOptions
{
    public const string SectionName = "Requests:Scheduled";

    /// <summary>
    /// Lead time before the scheduled moment at which matching opens.
    /// Acceptance criterion mandates 30 minutes; configurable so ops can
    /// adjust per market without a redeploy.
    /// </summary>
    public TimeSpan MatchingBuffer { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the activator scans for scheduled rows whose matching
    /// window has opened. 30 seconds matches the request-expiry sweeper
    /// cadence so the two background services have similar load profiles.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
