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
/// RECORD-OF-TRUTH GATE. The canonical per-party rating is persisted upstream
/// only when <c>FeatureFlags:UseUpstream:Feedback</c> is on, in which case it is
/// written to the REAL <c>feedback-service</c> (host port 10064) via
/// <see cref="IFeedbackServiceClient"/> before the gateway updates its own
/// blind-reveal state. When the flag is off, the in-memory
/// <see cref="IRatingStore"/> is the record-of-truth (the legacy MVP path) and
/// no upstream call is made.
/// </para>
///
/// <para>
/// The previously-wired <see cref="IScoreServiceClient"/> targeted a STALE
/// <c>score-taking-service</c> (no appsettings entry anywhere; not in the
/// deployed fleet) and is no longer the record-of-truth. It is retained as a
/// constructor dependency only for backward-compatible DI/test construction;
/// it is never invoked.
/// </para>
/// </summary>
public sealed class RatingService : IRatingService
{
    private const int MinStars = 1;
    private const int MaxStars = 5;

    private readonly IRequestsStore _requests;
    private readonly IRatingStore _ratings;
    private readonly IScoreServiceClient _scoreClient;
    private readonly IFeedbackServiceClient _feedbackClient;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;
    private readonly TimeProvider _clock;
    private readonly RatingOptions _options;
    private readonly ILogger<RatingService> _log;

    public RatingService(
        IRequestsStore requests,
        IRatingStore ratings,
        IScoreServiceClient scoreClient,
        IFeedbackServiceClient feedbackClient,
        IOptionsMonitor<UpstreamFeatureFlags> flags,
        TimeProvider clock,
        IOptions<RatingOptions> options,
        ILogger<RatingService> log)
    {
        _requests = requests;
        _ratings = ratings;
        _scoreClient = scoreClient;
        _feedbackClient = feedbackClient;
        _flags = flags;
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

        // Record-of-truth gate (FeatureFlags:UseUpstream:Feedback). When ON,
        // persist the canonical per-party rating to the REAL feedback-service
        // BEFORE updating the local store. If the downstream call fails we
        // surface the error; the local store stays empty so a retry is safe.
        // When OFF, the in-memory store is the record-of-truth (legacy MVP
        // path) and no upstream call is made.
        var rateeUserId = callerIsClient ? delivery.JeeberId! : delivery.ClientId;
        if (_flags.CurrentValue.Feedback)
        {
            try
            {
                await _feedbackClient.SubmitCommentAsync(new FeedbackSubmitRequest
                {
                    // The review topic is the ratee — aggregate ratings are
                    // queried per ratee (a user's received-rating average).
                    Tag = rateeUserId,
                    CommenterId = callerUserId,
                    Rating = stars,
                    Criteria = callerIsClient ? "client" : "jeeber",
                    Text = trimmed,
                }, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "feedback-service submission failed for delivery {DeliveryId} author {AuthorUserId}",
                    deliveryId, callerUserId);
                throw;
            }
        }

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
