namespace JeebGateway.Requests.IdentityReveal;

/// <summary>
/// DOUBLE-BLIND IDENTITY REVEAL — feature <c>double-blind-reveal</c>
/// (server-owned policy, JEEB double-blind product decision).
///
/// <para>
/// The customer and the Jeeber never see each other's real identity until the
/// delivery lifecycle reaches the point where it is operationally necessary.
/// This is the counterpart to <see cref="JeebGateway.Ratings.BlindRevealPolicy"/>
/// (which blinds the post-delivery RATINGS); this type blinds the in-flight
/// PARTY IDENTITY (name / phone / avatar / rating chip).
/// </para>
///
/// <para>
/// The rule is intentionally a pure, side-effect-free projection over the
/// delivery <c>status</c> so it can be unit-tested combinatorially and so the
/// SAME verdict is produced no matter which controller asks. The gateway owns
/// this rule (ADR-0001: BFF composes; the policy is presentation-shaping, not a
/// domain mutation) — the mobile client is NOT trusted to decide what it may
/// show. The client merely renders whatever scoped slice the gateway returns.
/// </para>
///
/// <para>Reveal ladder (mirrors <c>RequestStatus</c> and the mobile
/// <c>DeliveryStage</c>):</para>
/// <list type="bullet">
///   <item><b>Pre-match</b> (<c>scheduled</c>, <c>pending</c>, <c>matched</c>):
///     no Jeeber is bound yet → <see cref="IdentityRevealLevel.Hidden"/>. Nothing
///     is exposed.</item>
///   <item><b>Matched/accepted</b> (<c>accepted</c>): a Jeeber is bound but has
///     not yet taken custody → <see cref="IdentityRevealLevel.NamePreview"/>.
///     Short display name + vehicle + rating chip; phone WITHHELD (anti-spam —
///     contact is brokered through the masked-call channel, not a raw number).</item>
///   <item><b>In custody / in transit</b> (<c>picked_up</c>, <c>heading_off</c>,
///     <c>at_door</c>): the parties must be able to coordinate hand-off →
///     <see cref="IdentityRevealLevel.Contactable"/>. Name + vehicle + rating +
///     dialable phone are exposed.</item>
///   <item><b>Terminal</b> (<c>delivered</c>, <c>rated</c>): the operational need
///     to contact has passed → revert to <see cref="IdentityRevealLevel.NamePreview"/>
///     (the receipt still shows who handled the parcel, but the live phone channel
///     is closed). <c>cancelled</c>/<c>expired</c> → <see cref="IdentityRevealLevel.Hidden"/>.</item>
/// </list>
/// </summary>
public static class IdentityRevealPolicy
{
    /// <summary>
    /// Statuses at which the counterpart is contactable (live phone exposed).
    /// Strictly the in-custody window — the only span where a raw dialable
    /// number is justified.
    /// </summary>
    private static readonly IReadOnlySet<string> ContactableStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.PickedUp,
            RequestStatus.HeadingOff,
            RequestStatus.AtDoor,
        };

    /// <summary>
    /// Statuses at which the counterpart's name/rating may be previewed but the
    /// phone stays withheld: bound-but-not-yet-in-custody, plus the post-delivery
    /// receipt view.
    /// </summary>
    private static readonly IReadOnlySet<string> NamePreviewStates =
        new HashSet<string>(StringComparer.Ordinal)
        {
            RequestStatus.Accepted,
            RequestStatus.Delivered,
            RequestStatus.Rated,
            // A client-requested cancellation pending admin sign-off keeps the
            // parties bound; keep the name (but not the phone) visible so the UI
            // can still show who is involved while the dispute resolves.
            RequestStatus.CancellationRequested,
            RequestStatus.Disputed,
        };

    /// <summary>
    /// Projects the reveal level the caller is allowed to see for the given
    /// delivery <paramref name="status"/>. Pure: status in, level out.
    /// </summary>
    /// <param name="status">The canonical delivery status (see <see cref="RequestStatus"/>).</param>
    /// <param name="counterpartBound">
    /// True when a counterpart id actually exists on the row (a Jeeber has been
    /// bound). Even at a name-preview status, nothing can be revealed if no
    /// counterpart is bound yet — defends against a status/row mismatch.
    /// </param>
    public static IdentityRevealLevel LevelFor(string status, bool counterpartBound)
    {
        if (string.IsNullOrWhiteSpace(status)) return IdentityRevealLevel.Hidden;
        if (!counterpartBound) return IdentityRevealLevel.Hidden;

        if (ContactableStates.Contains(status)) return IdentityRevealLevel.Contactable;
        if (NamePreviewStates.Contains(status)) return IdentityRevealLevel.NamePreview;

        // Pre-match (scheduled/pending/matched) and terminal-negative
        // (cancelled/expired) reveal nothing.
        return IdentityRevealLevel.Hidden;
    }

    /// <summary>True when the level permits exposing a dialable phone number.</summary>
    public static bool IsPhoneVisible(IdentityRevealLevel level) =>
        level == IdentityRevealLevel.Contactable;

    /// <summary>True when the level permits exposing name/vehicle/rating.</summary>
    public static bool IsNameVisible(IdentityRevealLevel level) =>
        level is IdentityRevealLevel.NamePreview or IdentityRevealLevel.Contactable;
}

/// <summary>
/// How much of the counterpart's identity the caller may see at the current
/// delivery lifecycle point. Ordered least → most exposure.
/// </summary>
public enum IdentityRevealLevel
{
    /// <summary>Nothing is exposed (pre-match, or cancelled/expired).</summary>
    Hidden,

    /// <summary>Name + vehicle + rating chip; phone withheld.</summary>
    NamePreview,

    /// <summary>Name + vehicle + rating + dialable phone (in-custody window).</summary>
    Contactable,
}
