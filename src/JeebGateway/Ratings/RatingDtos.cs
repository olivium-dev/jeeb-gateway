namespace JeebGateway.Ratings;

/// <summary>
/// POST /api/deliveries/{id}/rate body. Stars must be 1..5; comment is optional
/// and capped at <see cref="MaxCommentLength"/> characters by the service.
/// </summary>
public sealed class SubmitRatingRequest
{
    public const int MaxCommentLength = 1000;

    public int Stars { get; set; }
    public string? Comment { get; set; }
}

/// <summary>
/// Public view of one rating party. The counterparty's <see cref="Stars"/>
/// and <see cref="Comment"/> are scrubbed to null while the rating is still
/// blind (see <see cref="BlindRevealPolicy"/>).
/// </summary>
public sealed class RatingPartyView
{
    public bool Submitted { get; init; }
    public int? Stars { get; init; }
    public string? Comment { get; init; }
    public DateTimeOffset? SubmittedAt { get; init; }
}

/// <summary>
/// GET /api/deliveries/{id}/rating response. Encodes both sides of the
/// mutual-blind state machine so the mobile app can render a single screen
/// without an extra trip.
/// </summary>
public sealed class RatingStatusResponse
{
    public required string DeliveryId { get; init; }

    /// <summary>One of <c>pending_mine</c>, <c>pending_theirs</c>,
    /// <c>revealed</c>, <c>locked_no_rating</c>.</summary>
    public required string State { get; init; }

    public required RatingPartyView Mine { get; init; }
    public required RatingPartyView Theirs { get; init; }

    public required DateTimeOffset WindowClosesAt { get; init; }
    public required bool WindowExpired { get; init; }
}

/// <summary>
/// Response returned by POST /api/deliveries/{id}/rate. Same shape as the
/// GET — the client uses it to refresh the screen in one round trip.
/// </summary>
public sealed class SubmitRatingResponse
{
    public required RatingStatusResponse Status { get; init; }
}

/// <summary>
/// Wire-mapping helpers. Centralised so the controller and tests render the
/// same string for each <see cref="BlindRevealOutcome"/>.
/// </summary>
public static class RatingStateCodes
{
    public const string PendingMine = "pending_mine";
    public const string PendingTheirs = "pending_theirs";
    public const string Revealed = "revealed";
    public const string LockedNoRating = "locked_no_rating";

    /// <summary>T-BE-025 / JEB-61 wire constant. The cron flips a stale
    /// row to this state 7 days after delivery when at least one side
    /// hasn't submitted; the missing side's stars stay null.</summary>
    public const string AutoRevealed = "auto_revealed";

    public static string For(BlindRevealOutcome outcome) => outcome switch
    {
        BlindRevealOutcome.PendingMine => PendingMine,
        BlindRevealOutcome.PendingTheirs => PendingTheirs,
        BlindRevealOutcome.Revealed => Revealed,
        BlindRevealOutcome.LockedNoRating => LockedNoRating,
        BlindRevealOutcome.AutoRevealed => AutoRevealed,
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null)
    };
}
