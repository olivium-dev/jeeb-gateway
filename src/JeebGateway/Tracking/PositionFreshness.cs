namespace JeebGateway.Tracking;

/// <summary>
/// How much the gateway can vouch for a Jeeber's last known position.
///
/// <para><b>Why this type exists.</b> The tracking snapshot used to carry a
/// single <c>stale</c> boolean computed as
/// <c>latest is not null &amp;&amp; (now - latest.ReceivedAt) &gt; StaleThreshold</c>.
/// That expression is <c>false</c> whenever <c>latest</c> is null, so the two
/// most different states on this axis — "the courier has not started reporting"
/// and "the courier has gone missing" — serialised byte-identically as
/// <c>position:null, stale:false, secondsSinceUpdate:null</c>. A client holding a
/// previously-rendered marker was told "everything is fine" for a courier it had
/// lost, and left the pin on the map as if it were live. That is the phantom pin.</para>
///
/// <para>The four states below are ordered by decreasing confidence and are a
/// pure function of one fix's age, so the ladder is monotonic: a position can go
/// <see cref="Live"/> → <see cref="Stale"/> → <see cref="Lost"/> as it ages, and
/// never backwards without a fresh fix arriving.</para>
/// </summary>
public enum PositionFreshness
{
    /// <summary>
    /// No fix has ever been recorded for this Jeeber (or the last one aged past
    /// <see cref="TrackingOptions.PositionRetention"/> and was dropped). The
    /// client paints "awaiting first ping" — there is nothing to show and
    /// nothing has been lost.
    /// </summary>
    AwaitingFirstFix = 0,

    /// <summary>
    /// The last fix is younger than <see cref="TrackingOptions.StaleThreshold"/>.
    /// Render the marker normally.
    /// </summary>
    Live = 1,

    /// <summary>
    /// The last fix is older than <see cref="TrackingOptions.StaleThreshold"/> but
    /// still within <see cref="TrackingOptions.PositionTtl"/>. The coordinates are
    /// still published — a stationary courier legitimately produces no uploads for
    /// minutes (the mobile uploader uses <c>distanceFilter: 10</c>) — but the
    /// client must degrade the marker rather than present it as live.
    /// </summary>
    Stale = 2,

    /// <summary>
    /// The last fix is older than <see cref="TrackingOptions.PositionTtl"/>. We had
    /// a courier and we no longer know where they are. The coordinates are NOT
    /// published, so no client can render a pin from them, but
    /// <c>secondsSinceUpdate</c> still is — that non-null age beside a null
    /// position is what distinguishes this from <see cref="AwaitingFirstFix"/>.
    /// </summary>
    Lost = 3,
}

/// <summary>
/// The single place the tracking wire's freshness verdict is derived. Every
/// consumer that needs to know whether a stored fix is current asks here rather
/// than re-deriving a threshold comparison, so the ladder cannot drift between
/// the snapshot endpoint and any other reader.
/// </summary>
public static class TrackingFreshness
{
    /// <summary>
    /// Classify a stored fix by age. Pure: no clock of its own, no store access.
    /// </summary>
    /// <param name="latest">
    /// The last fix the store holds, or <c>null</c> when it holds none. Note the
    /// store no longer returns <c>null</c> merely because a fix is old — see
    /// <see cref="ILocationStore.GetLatestAsync"/> — so <c>null</c> here really
    /// does mean "nothing on record".
    /// </param>
    /// <param name="now">Server clock reading to measure age against.</param>
    /// <param name="options">The configured ladder thresholds.</param>
    public static PositionFreshness Classify(StoredPosition? latest, DateTimeOffset now, TrackingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (latest is null) return PositionFreshness.AwaitingFirstFix;

        var age = now - latest.ReceivedAt;
        if (age > options.PositionTtl) return PositionFreshness.Lost;
        if (age > options.StaleThreshold) return PositionFreshness.Stale;
        return PositionFreshness.Live;
    }

    /// <summary>
    /// True when the position's coordinates may be published on the wire. False
    /// for <see cref="PositionFreshness.Lost"/> so that a client which ignores
    /// every new field still cannot draw a pin for a courier we have lost — the
    /// pre-fix behaviour past the TTL, preserved deliberately.
    /// </summary>
    public static bool PublishesCoordinates(this PositionFreshness freshness) =>
        freshness is PositionFreshness.Live or PositionFreshness.Stale;

    /// <summary>
    /// The legacy <c>stale</c> boolean. True for BOTH <see cref="PositionFreshness.Stale"/>
    /// and <see cref="PositionFreshness.Lost"/>: a client that only reads this
    /// field must never be told "fine" about a courier whose position we cannot
    /// vouch for. This is the half of the fix that reaches clients which have not
    /// yet adopted <c>positionStatus</c>.
    /// </summary>
    public static bool IsStale(this PositionFreshness freshness) =>
        freshness is PositionFreshness.Stale or PositionFreshness.Lost;

    /// <summary>
    /// Wire value for <c>TrackingPolylineDto.PositionStatus</c>. Serialised as a
    /// string rather than the enum so the JSON contract does not depend on
    /// member ordering or on a global enum-converter registration.
    /// </summary>
    public static string ToWireValue(this PositionFreshness freshness) => freshness switch
    {
        PositionFreshness.Live => "live",
        PositionFreshness.Stale => "stale",
        PositionFreshness.Lost => "lost",
        _ => "awaitingFirstFix",
    };
}
