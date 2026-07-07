namespace JeebGateway.Ratings;

/// <summary>
/// T-backend-020 / JEEB-38 — pure projection logic for the mutual-blind
/// rating rule.
///
/// Inputs: (now, deliveredAt, myRating, theirRating, ratingWindow).
/// Output: a <see cref="BlindRevealView"/> describing what each side may
/// see and whether the rating window is still open.
///
/// Rule set:
/// <list type="bullet">
///   <item>If the caller has not submitted yet AND the window is open, both
///     sides return <see cref="BlindRevealOutcome.PendingMine"/>; the
///     caller's own rating is null, the counterparty's is hidden.</item>
///   <item>If the caller has submitted but the counterparty has not AND the
///     window is open, return <see cref="BlindRevealOutcome.PendingTheirs"/>;
///     the caller sees their own rating, but NOT the counterparty's.</item>
///   <item>If both sides have submitted (regardless of window), return
///     <see cref="BlindRevealOutcome.Revealed"/>; both ratings are visible.</item>
///   <item>If the window has closed (now &gt; deliveredAt + window) and at least
///     one side did not submit, return
///     <see cref="BlindRevealOutcome.LockedNoRating"/>; the row is now
///     immutable and no one-sided rating is revealed.</item>
/// </list>
///
/// This type is intentionally static + side-effect free so unit tests can
/// exercise the rule combinatorially without mocking HTTP clients or stores.
/// </summary>
public static class BlindRevealPolicy
{
    /// <summary>Default rating window from the spec — 7 days after delivery.</summary>
    public static readonly TimeSpan DefaultRatingWindow = TimeSpan.FromDays(7);

    /// <summary>
    /// Computes the view a single party should see, given which side they are.
    /// </summary>
    /// <param name="now">Wall-clock reference for window evaluation.</param>
    /// <param name="deliveredAt">When the delivery transitioned to delivered.</param>
    /// <param name="callerIsClient">True if the caller is the Client side.</param>
    /// <param name="clientRating">Client-side rating, or null when not yet submitted.</param>
    /// <param name="jeeberRating">Jeeber-side rating, or null when not yet submitted.</param>
    /// <param name="ratingWindow">How long ratings may be submitted after delivery.</param>
    public static BlindRevealView ProjectFor(
        DateTimeOffset now,
        DateTimeOffset deliveredAt,
        bool callerIsClient,
        RatingEntry? clientRating,
        RatingEntry? jeeberRating,
        TimeSpan ratingWindow)
    {
        var windowClosesAt = deliveredAt + ratingWindow;
        var windowExpired = now > windowClosesAt;
        var bothSubmitted = clientRating is not null && jeeberRating is not null;

        var mine = callerIsClient ? clientRating : jeeberRating;
        var theirs = callerIsClient ? jeeberRating : clientRating;

        // Both submitted — always revealed, even if past the window.
        if (bothSubmitted)
        {
            return new BlindRevealView(
                Outcome: BlindRevealOutcome.Revealed,
                MyRating: mine,
                TheirRating: theirs,
                WindowClosesAt: windowClosesAt,
                WindowExpired: windowExpired);
        }

        // Window closed without both sides submitting - locked without revealing
        // any one-sided rating.
        if (windowExpired)
        {
            return new BlindRevealView(
                Outcome: BlindRevealOutcome.LockedNoRating,
                MyRating: null,
                TheirRating: null,
                WindowClosesAt: windowClosesAt,
                WindowExpired: true);
        }

        // Window still open. Caller hasn't submitted yet → PendingMine.
        if (mine is null)
        {
            return new BlindRevealView(
                Outcome: BlindRevealOutcome.PendingMine,
                MyRating: null,
                TheirRating: null,
                WindowClosesAt: windowClosesAt,
                WindowExpired: false);
        }

        // Caller submitted, counterparty hasn't — show mine, hide theirs.
        return new BlindRevealView(
            Outcome: BlindRevealOutcome.PendingTheirs,
            MyRating: mine,
            TheirRating: null,
            WindowClosesAt: windowClosesAt,
            WindowExpired: false);
    }
}

/// <summary>
/// Outcome bucket returned by <see cref="BlindRevealPolicy.ProjectFor"/>.
/// </summary>
public enum BlindRevealOutcome
{
    /// <summary>Caller has not yet rated; rating window still open.</summary>
    PendingMine,

    /// <summary>Caller has rated; counterparty has not yet rated and the
    /// window is still open. Caller sees their own rating only.</summary>
    PendingTheirs,

    /// <summary>Both sides submitted. Each side sees the other.</summary>
    Revealed,

    /// <summary>Window closed without both sides submitting. Row is locked;
    /// no further submissions allowed.</summary>
    LockedNoRating,
}

/// <summary>One party's rating capture. Stars in [1,5], optional comment.</summary>
public sealed record RatingEntry(
    string AuthorUserId,
    int Stars,
    string? Comment,
    DateTimeOffset SubmittedAt);

/// <summary>
/// What a single caller may observe about the rating row at a moment in time.
/// </summary>
public sealed record BlindRevealView(
    BlindRevealOutcome Outcome,
    RatingEntry? MyRating,
    RatingEntry? TheirRating,
    DateTimeOffset WindowClosesAt,
    bool WindowExpired);
