namespace JeebGateway.Requests;

/// <summary>
/// P7 — THE single arithmetic for the offer-wait window.
///
/// The sweeper (commit side, <see cref="RequestExpirySweeper"/>) and the read
/// projection (client-visible countdown, <see cref="OfferDeadlineProjector"/>)
/// MUST both go through here — that is the guarantee a stored
/// <c>gw_expires_at</c> column was being asked to provide, at no migration and
/// no new DB seam (design ace66689, "the defect that was hiding"). The offer
/// deadline is DERIVED, never stored; a parity test
/// (<c>RequestExpiryMathParityTests</c>) makes drift impossible.
/// </summary>
public static class RequestExpiryMath
{
    /// <summary>
    /// Absolute UTC instant the offer-wait window closes for <paramref name="r"/>,
    /// or <c>null</c> unless the row is in the offer-wait window
    /// (<see cref="RequestStatus.PreAcceptanceStates"/> = {pending, matched}).
    /// </summary>
    public static DateTimeOffset? DeadlineFor(
        DeliveryRequest r,
        IReadOnlyDictionary<string, TimeSpan> ttls,
        TierExpiryWindowResolver windows)
        => RequestStatus.IsPreAcceptance(r.Status)
            ? r.CreatedAt + windows.ResolveExpiryWindow(r, ttls)
            : null;

    /// <summary>
    /// Seconds left at <paramref name="now"/>, clamped at 0. Null EXACTLY when
    /// <paramref name="deadline"/> is null. Ceiling so a 0.4 s remainder reads 1,
    /// not 0 — 0 is reserved to mean "the window has closed".
    /// </summary>
    public static int? RemainingSeconds(DateTimeOffset? deadline, DateTimeOffset now)
        => deadline is null
            ? null
            : (int)Math.Max(0d, Math.Ceiling((deadline.Value - now).TotalSeconds));

    /// <summary>
    /// True when the sweeper should terminally expire this row at
    /// <paramref name="now"/>. The boundary is INCLUSIVE (<c>d &lt;= now</c>) so
    /// the instant the projector reports 0 remaining is the instant the sweeper
    /// is willing to expire.
    /// </summary>
    public static bool IsExpiredAt(
        DeliveryRequest r,
        IReadOnlyDictionary<string, TimeSpan> ttls,
        TierExpiryWindowResolver windows,
        DateTimeOffset now)
        => DeadlineFor(r, ttls, windows) is { } d && d <= now;
}
