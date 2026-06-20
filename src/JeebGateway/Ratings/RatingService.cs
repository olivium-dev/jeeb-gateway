using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Ratings;

/// <summary>
/// T-backend-020 / JEEB-38 — see <see cref="IRatingService"/>.
///
/// <para>
/// RECORD-OF-TRUTH. The in-memory <see cref="IRatingStore"/> is the
/// record-of-truth for the per-delivery mutual-blind rating pairing and the
/// 7-day reveal projection (<see cref="BlindRevealPolicy"/>). This blind-reveal
/// pairing is a jeeb-specific delivery concept with no upstream owner.
/// </para>
///
/// <para>
/// FEEDBACK-SERVICE NOTE. The generic feedback-service comment/rating surface is
/// now owned entirely by the salehly-mirrored
/// <see cref="JeebGateway.Controllers.FeedbackController"/> (an always-on
/// passthrough over <c>ServiceFeedbackClient</c>, exactly as the salehly sibling
/// ships it). The former jeeb-specific path that flag-gated a per-party rating
/// write into feedback-service from this service (the hand-coded
/// <c>IFeedbackServiceClient</c> behind <c>FeatureFlags:UseUpstream:Feedback</c>)
/// was removed with the literal salehly replace — that hand-coded client and
/// flag no longer exist.
/// </para>
///
/// <para>
/// SCORE-TAKING REMOVED. A previously-wired score-taking-service client was deleted
/// completely (owner directive: never use score-taking). It was never the
/// record-of-truth and was never invoked. The mutual-blind pairing store
/// (<see cref="IRatingStore"/>) — optionally backed by feedback-service behind
/// <c>FeatureFlags:UseUpstream:Ratings</c> — is the only persistence path.
/// </para>
/// </summary>
public sealed class RatingService : IRatingService
{
    private const int MinStars = 1;
    private const int MaxStars = 5;

    private readonly IRequestsStore _requests;
    private readonly IRatingStore _ratings;
    private readonly TimeProvider _clock;
    private readonly RatingOptions _options;
    private readonly ILogger<RatingService> _log;

    public RatingService(
        IRequestsStore requests,
        IRatingStore ratings,
        TimeProvider clock,
        IOptions<RatingOptions> options,
        ILogger<RatingService> log)
    {
        _requests = requests;
        _ratings = ratings;
        _clock = clock;
        _options = options.Value;
        _log = log;
    }

    public async Task<RatingSubmissionResult> SubmitAsync(
        string deliveryId,
        string callerUserId,
        int stars,
        string? comment,
        CancellationToken ct)
    {
        if (stars < MinStars || stars > MaxStars)
        {
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.InvalidStars, null, false,
                $"stars must be between {MinStars} and {MaxStars}.");
        }

        var trimmed = comment?.Trim();
        if (trimmed is not null && trimmed.Length > SubmitRatingRequest.MaxCommentLength)
        {
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.CommentTooLong, null, false,
                $"comment exceeds {SubmitRatingRequest.MaxCommentLength} characters.");
        }

        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.DeliveryNotFound, null, false, null);
        }

        if (!IsRateableStatus(delivery.Status))
        {
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.NotDelivered, null, false,
                $"delivery status is '{delivery.Status}'.");
        }

        if (string.IsNullOrEmpty(delivery.JeeberId))
        {
            // Defensive: a row that reached delivered/rated must have a Jeeber.
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.NotDelivered, null, false,
                "delivery has no Jeeber bound.");
        }

        var callerIsClient = string.Equals(delivery.ClientId, callerUserId, StringComparison.Ordinal);
        var callerIsJeeber = string.Equals(delivery.JeeberId, callerUserId, StringComparison.Ordinal);
        if (!callerIsClient && !callerIsJeeber)
        {
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.NotAParty, null, false, null);
        }

        var now = _clock.GetUtcNow();
        var deliveredAt = ResolveDeliveredAt(delivery, now);
        var pair = await _ratings.EnsureAsync(
            deliveryId, delivery.ClientId, delivery.JeeberId!, deliveredAt, ct);

        // Window closed? Pre-flight check so we can short-circuit before
        // touching the downstream score service.
        if (now > pair.DeliveredAt + _options.RatingWindow)
        {
            var lockedView = BlindRevealPolicy.ProjectFor(
                now, pair.DeliveredAt, callerIsClient,
                pair.ClientRating, pair.JeeberRating, _options.RatingWindow);
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.WindowClosed, lockedView, callerIsClient,
                $"rating window closed at {pair.DeliveredAt + _options.RatingWindow:O}.");
        }

        var alreadySubmitted = callerIsClient
            ? pair.ClientRating is not null
            : pair.JeeberRating is not null;
        if (alreadySubmitted)
        {
            var view = BlindRevealPolicy.ProjectFor(
                now, pair.DeliveredAt, callerIsClient,
                pair.ClientRating, pair.JeeberRating, _options.RatingWindow);
            return new RatingSubmissionResult(
                RatingSubmissionOutcome.AlreadyRated, view, callerIsClient,
                "caller has already submitted a rating for this delivery.");
        }

        // Record-of-truth: the in-memory blind-reveal pairing store. The generic
        // feedback-service comment write surface is owned by FeedbackController
        // (salehly mirror); this jeeb-specific delivery rating no longer
        // double-writes there.
        var entry = new RatingEntry(
            AuthorUserId: callerUserId,
            Stars: stars,
            Comment: trimmed,
            SubmittedAt: now);

        var updated = await _ratings.SubmitAsync(deliveryId, callerIsClient, entry, ct);

        var resultView = BlindRevealPolicy.ProjectFor(
            now, updated.DeliveredAt, callerIsClient,
            updated.ClientRating, updated.JeeberRating, _options.RatingWindow);

        return new RatingSubmissionResult(
            RatingSubmissionOutcome.Submitted, resultView, callerIsClient, null);
    }

    public async Task<RatingQueryResult> GetAsync(
        string deliveryId,
        string callerUserId,
        CancellationToken ct)
    {
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null)
        {
            return new RatingQueryResult(RatingQueryOutcome.NotFound, null, false);
        }

        if (!IsRateableStatus(delivery.Status))
        {
            return new RatingQueryResult(RatingQueryOutcome.NotDelivered, null, false);
        }

        if (string.IsNullOrEmpty(delivery.JeeberId))
        {
            return new RatingQueryResult(RatingQueryOutcome.NotDelivered, null, false);
        }

        var callerIsClient = string.Equals(delivery.ClientId, callerUserId, StringComparison.Ordinal);
        var callerIsJeeber = string.Equals(delivery.JeeberId, callerUserId, StringComparison.Ordinal);
        if (!callerIsClient && !callerIsJeeber)
        {
            return new RatingQueryResult(RatingQueryOutcome.NotAParty, null, false);
        }

        var now = _clock.GetUtcNow();
        var deliveredAt = ResolveDeliveredAt(delivery, now);
        var pair = await _ratings.GetAsync(deliveryId, ct)
                   ?? await _ratings.EnsureAsync(deliveryId, delivery.ClientId, delivery.JeeberId!, deliveredAt, ct);

        var view = BlindRevealPolicy.ProjectFor(
            now, pair.DeliveredAt, callerIsClient,
            pair.ClientRating, pair.JeeberRating, _options.RatingWindow);

        return new RatingQueryResult(RatingQueryOutcome.Ok, view, callerIsClient);
    }

    /// <summary>
    /// Rating is only permitted on rows that have been delivered (or already
    /// rated by one side). The cancelled / expired / disputed terminals are
    /// out of scope.
    /// </summary>
    private static bool IsRateableStatus(string status) =>
        string.Equals(status, RequestStatus.Delivered, StringComparison.Ordinal)
        || string.Equals(status, RequestStatus.Rated, StringComparison.Ordinal);

    /// <summary>
    /// The delivery row does not currently carry an explicit delivered-at
    /// timestamp (see <see cref="DeliveryRequest"/>), so we use the
    /// strongest proxy available: <see cref="DeliveryRequest.AcceptedAt"/>
    /// + a generous fallback to <paramref name="now"/> when accept timing
    /// is also missing. The rating store stamps this on first call and
    /// keeps it stable for the rest of the row's life.
    /// </summary>
    private static DateTimeOffset ResolveDeliveredAt(DeliveryRequest delivery, DateTimeOffset now)
    {
        return delivery.AcceptedAt ?? delivery.CreatedAt;
    }
}

/// <summary>
/// Options for the rating module — wall-clock window after delivery during
/// which ratings may still be submitted (default 7 days per the JEEB-38
/// spec). Bound to configuration section <see cref="SectionName"/>.
/// </summary>
public sealed class RatingOptions
{
    public const string SectionName = "Ratings";

    public TimeSpan RatingWindow { get; set; } = BlindRevealPolicy.DefaultRatingWindow;
}
