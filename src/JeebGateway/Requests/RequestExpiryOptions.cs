namespace JeebGateway.Requests;

/// <summary>
/// Tunables for the request-expiry sweeper (T-backend-028).
///
/// Two windows govern a Client's open request:
///   * <see cref="NoOfferNudgeWindow"/> — if no Jeeber has even bid yet, the
///     Client is prompted to widen their tier (FR-6.6).
///   * <see cref="ExpiryWindow"/> — if no offer has been accepted within this
///     window, the request is moved to <c>expired</c> and the Client is told
///     to re-request. <see cref="ExpiryWindow"/> MUST be strictly greater
///     than <see cref="NoOfferNudgeWindow"/>.
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
    /// Time after creation at which a request without an accepted offer is
    /// terminally expired. 30 minutes per ticket acceptance criteria.
    /// </summary>
    public TimeSpan ExpiryWindow { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How often the sweeper scans for stale requests. Production runs at
    /// the 30-second cadence; tests drive <c>SweepOnceAsync</c> directly.
    /// </summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(30);
}
