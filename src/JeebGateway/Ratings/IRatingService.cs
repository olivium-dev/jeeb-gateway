namespace JeebGateway.Ratings;

/// <summary>
/// Orchestrates the mutual-blind rating flow. Owns the eligibility checks
/// (caller-is-a-party, status-is-delivered, window-still-open) and emits the
/// caller-specific view via <see cref="BlindRevealPolicy"/>.
///
/// Persistence is the gateway's own <see cref="IRatingStore"/> (in-memory by
/// default; optionally feedback-service-backed behind
/// <c>FeatureFlags:UseUpstream:Ratings</c>). The former score-taking-service
/// client was removed entirely (owner directive) and was never the
/// record-of-truth.
/// </summary>
public interface IRatingService
{
    /// <summary>
    /// Submits a rating for <paramref name="deliveryId"/> on behalf of
    /// <paramref name="callerUserId"/>. Returns the post-submit view.
    /// </summary>
    Task<RatingSubmissionResult> SubmitAsync(
        string deliveryId,
        string callerUserId,
        int stars,
        string? comment,
        CancellationToken ct);

    /// <summary>
    /// Returns the caller-specific view of the rating row, or a
    /// <see cref="RatingQueryOutcome.NotFound"/> result when no delivery
    /// matches.
    /// </summary>
    Task<RatingQueryResult> GetAsync(
        string deliveryId,
        string callerUserId,
        CancellationToken ct);
}

public enum RatingSubmissionOutcome
{
    Submitted,
    DeliveryNotFound,
    NotAParty,
    NotDelivered,
    AlreadyRated,
    WindowClosed,
    InvalidStars,
    CommentTooLong,
}

public sealed record RatingSubmissionResult(
    RatingSubmissionOutcome Outcome,
    BlindRevealView? View,
    bool CallerIsClient,
    string? Detail);

public enum RatingQueryOutcome
{
    Ok,
    NotFound,
    NotAParty,
    NotDelivered,
}

public sealed record RatingQueryResult(
    RatingQueryOutcome Outcome,
    BlindRevealView? View,
    bool CallerIsClient);
