namespace JeebGateway.Requests;

/// <summary>
/// Tunables for the request-expiry sweeper (T-backend-028).
///
/// Two windows govern a Client's open request:
///   * <see cref="NoOfferNudgeWindow"/> — if no Jeeber has even bid yet, the
///     Client is prompted to widen their tier (FR-6.6).
///   * the selected tier's request TTL — if no offer has been accepted within
///     that tier-specific window, the request is moved to <c>expired</c> and
///     the Client is told to re-request.
/// </summary>
public class RequestExpiryOptions
{
    public const string SectionName = "Requests:Expiry";

    /// <summary>
    /// Time after creation at which a Client whose request still has zero
    /// offers receives the "try expanding tier" push. 10 minutes per ticket
    /// acceptance criteria.
    /// </summary>
    public TimeSpan NoOfferNudgeWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How often the sweeper scans for stale requests. Production runs at
    /// the 30-second cadence; tests drive <c>SweepOnceAsync</c> directly.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How often the gateway observer polls delivery-service for expiry facts.
    /// </summary>
    public TimeSpan ObserverInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Maximum number of expired deliveries requested per observer pass. The
    /// delivery-service client clamps this value to the range 1..1000.
    /// </summary>
    public int ObserverBatchLimit { get; set; } = 200;

    /// <summary>
    /// Suppresses expiry pushes for rows whose expiry instant is earlier than
    /// this cutoff. Intended for one-time historical backfill without notifying
    /// users about stale requests; unset by default.
    /// </summary>
    public DateTimeOffset? SuppressNotifyBefore { get; set; }
}

/// <summary>
/// Rollout switch enforcing a single live TTL authority. <c>gateway</c> runs
/// the legacy <see cref="RequestExpirySweeper"/> and disables the observer;
/// <c>delivery-service</c> runs the observer and disables the sweeper. A gap is
/// safe because the upstream time-based sweep catches up, but an overlap is not.
/// Delete this switch in the cleanup PR after delivery-service has soaked.
/// </summary>
public sealed class RequestExpirySourceOptions
{
    public const string SectionName = "FeatureFlags:RequestExpiry";

    /// <summary>
    /// Selects the active TTL authority: <c>gateway</c> or
    /// <c>delivery-service</c>.
    /// </summary>
    public string Source { get; set; } = "gateway";

    /// <summary>
    /// Whether the stateless gateway observer should run.
    /// </summary>
    public bool ObserverEnabled => string.Equals(
        Source,
        "delivery-service",
        StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the legacy gateway TTL sweeper should run.
    /// </summary>
    public bool GatewaySweeperEnabled => !ObserverEnabled;
}
