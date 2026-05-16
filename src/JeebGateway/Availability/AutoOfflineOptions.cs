namespace JeebGateway.Availability;

public class AutoOfflineOptions
{
    public const string SectionName = "Availability:AutoOffline";

    /// <summary>
    /// How long a Jeeber may go without a GPS heartbeat or in-app
    /// interaction before being flipped offline. Defaults to 30 minutes
    /// per T-backend-023 acceptance criteria.
    /// </summary>
    public TimeSpan InactivityWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the sweeper scans for stale Jeebers. Production runs
    /// at the 60-second cadence; tests override to a tighter loop.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(60);
}
