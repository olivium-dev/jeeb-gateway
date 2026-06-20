using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.JeebReviews;
using JeebGateway.Ratings;
using JeebGateway.Ratings.Jeeb;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using JeebGateway.service.ServiceFeedback;
using FeedbackApiException = JeebGateway.service.ServiceFeedback.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// The Jeeb reviews/ratings FAMILY surface the mobile app consumes
/// (<c>DioReviewsRepository</c> / <c>DioRatingRepository</c>), filling out the
/// members the existing <see cref="JeebRatingsController"/>
/// (<c>v1/ratings/jeeb/deliveries/{deliveryId}</c>) does not expose:
///
/// <list type="bullet">
///   <item><c>GET  /v1/ratings/jeeb/reviews?jeeberId=&amp;page=&amp;pageSize=</c> — the
///     per-jeeber reviews list (R1m).</item>
///   <item><c>POST /v1/ratings/jeeb/reviews/{reviewId}/report</c> — report a review for
///     moderation (D27).</item>
///   <item><c>POST /v1/ratings/jeeb/submit</c> — submit one side of a delivery's
///     mutual-blind rating (the mobile <c>/submit</c> path shape).</item>
///   <item><c>GET  /v1/ratings/jeeb/{deliveryId}/status</c> — the caller's reveal-state
///     view, shaped as <c>{ deliveryId, state, ratings, ratedCount }</c>.</item>
/// </list>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): this controller authenticates, resolves the
/// delivery parties / caller's own id from the bearer token, maps to the EXISTING
/// generic <see cref="ServiceFeedbackClient"/> (the ratings record-of-truth; the
/// score-taking-service is DECOMMISSIONED), applies the Jeeb presentation
/// projection (<see cref="JeebReviewsProjection"/> + the shared
/// <see cref="JeebRatingVocabulary"/>), and returns. It holds NO state, NO
/// persistence, NO session and NO domain rules. The generic feedback-service stays
/// product-agnostic; all Jeeb vocabulary lives in the gateway projection (GR2),
/// mirroring <see cref="JeebRatingsController"/> and <see cref="JeebWalletController"/>.
/// </para>
///
/// <para>
/// Coverage note: the generic <see cref="ServiceFeedbackClient"/> exposes the
/// two-party blind-rating submit/reveal primitive (and a per-tag AVERAGE read) but
/// NO per-jeeber reviews LIST and NO review report/moderation endpoint. Until those
/// generic reads exist, the reviews-list route returns the correctly-shaped
/// COLD-START EMPTY page the mobile <c>DioReviewsRepository</c> already tolerates,
/// and the report route accepts the request (202 Accepted) rather than fabricating
/// moderation state in the (stateless) gateway. Re-point these to the generic reads
/// the moment feedback-service ships them — the mobile-facing contract is stable.
/// </para>
/// </summary>
[ApiController]
[Route("v1/ratings/jeeb")]
public sealed class JeebReviewsController : ControllerBase
{
    private const int MinStars = 1;
    private const int MaxStars = 5;

    /// <summary>180-day TTL on a durable review-report row (seconds).</summary>
    private const int ReportTtlSeconds = 180 * 24 * 60 * 60;

    private readonly ServiceFeedbackClient _feedback;
    private readonly IRequestsStore _requests;
    private readonly JeebGateway.StateService.Idempotency.IIdempotencyStore _reports;
    private readonly ILogger<JeebReviewsController> _log;

    public JeebReviewsController(
        ServiceFeedbackClient feedback,
        IRequestsStore requests,
        JeebGateway.StateService.Idempotency.IIdempotencyStore reports,
        ILogger<JeebReviewsController> log)
    {
        _feedback = feedback;
        _requests = requests;
        _reports = reports;
        _log = log;
    }

    /// <summary>
    /// GET /v1/ratings/jeeb/reviews?jeeberId=&amp;page=&amp;pageSize= — one page of a
    /// jeeber's reviews (R1m). See class remarks: the generic feedback-service has no
    /// per-jeeber reviews LIST, so this returns the mobile-tolerated cold-start empty
    /// page rather than synthesising review rows (ADR-0001).
    /// </summary>
    [HttpGet("reviews")]
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(typeof(JeebReviewsPageResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListReviews(
        [FromQuery] string? jeeberId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(jeeberId))
        {
            return BadRequest(Problem400("jeeberId query parameter is required."));
        }

        var id = jeeberId.Trim();
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;
        var offset = (safePage - 1) * safeSize;

        // Map the Jeeb user id to the SAME opaque rateeId the rating SUBMIT path stamps
        // (FeedbackServiceRatingStore.StableGuid), then read the jeeber's revealed reviews
        // from the SHARED feedback-service list-by-ratee surface (the ratings
        // record-of-truth; score-taking-service is DECOMMISSIONED). The Jeeb presentation
        // shaping stays here (ADR-0001 thin BFF / GR2); the upstream is opaque.
        var rateeId = FeedbackServiceRatingStore.StableGuid(id);

        try
        {
            var upstream = await _feedback.RatingsByRateeAsync(rateeId, safeSize, offset, ct);
            return Ok(JeebReviewsProjection.ProjectReviewsPage(id, upstream, safePage, safeSize));
        }
        catch (FeedbackApiException ex)
        {
            // Graceful degrade (ADR-0001): a transient upstream fault returns the
            // mobile-tolerated cold-start empty page rather than a 5xx — the reviews
            // surface is non-critical and the mobile DioReviewsRepository tolerates it.
            _log.LogWarning(
                "reviews list for jeeber {JeeberId} degraded to empty: upstream status {Status}",
                id, ex.StatusCode);
            return Ok(JeebReviewsProjection.EmptyReviewsPage(id, safePage, safeSize));
        }
    }

    /// <summary>
    /// POST /v1/ratings/jeeb/reviews/{reviewId}/report — report a review for
    /// moderation (D27). The generic feedback-service has no review-moderation
    /// endpoint, so rather than a pure no-op the gateway DURABLY records the report
    /// intent as an opaque row in jeeb-state-service (idempotent per
    /// (reviewId, reporter)) so a moderation worker can drain the queue later, then
    /// returns 202 Accepted. ADR-0001 preserved: the gateway holds no moderation
    /// state in-process; the row lives downstream and survives a bounce. The mobile
    /// repo only awaits a non-error response.
    /// </summary>
    [HttpPost("reviews/{reviewId}/report")]
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ReportReview(string reviewId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var reporterId, out var unauthorized)) return unauthorized;

        if (string.IsNullOrWhiteSpace(reviewId))
        {
            return BadRequest(Problem400("reviewId is required."));
        }

        // Durably record the report intent (opaque, idempotent) so it is not lost on a
        // gateway bounce — a moderation worker can drain "review-report:*" later. A
        // state-service blip never blocks the user: the report is best-effort.
        try
        {
            var key = $"review-report:{reviewId.Trim()}:{reporterId}";
            var body = System.Text.Json.JsonSerializer.Serialize(new
            {
                reviewId = reviewId.Trim(),
                reporterId,
                reportedAt = DateTimeOffset.UtcNow,
            });
            await _reports.PutOrGetAsync(key, statusCode: 202, body, ReportTtlSeconds, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "review-report durable record failed for review {ReviewId}; accepting anyway", reviewId);
        }

        // No generic moderation endpoint exists; the request is accepted for later
        // routing once feedback-service ships a report surface. No state is stored.
        return Accepted();
    }

    /// <summary>
    /// POST /v1/ratings/jeeb/submit — submit one side of a delivery's mutual-blind
    /// rating (the mobile <c>/submit</c> path shape; body carries <c>deliveryId</c>).
    /// Maps onto the SHARED feedback-service blind-rating primitive via the same
    /// <see cref="JeebRatingVocabulary"/> the deliveries-scoped route uses. Returns
    /// the projected status envelope.
    /// </summary>
    [HttpPost("submit")]
    [RequireCapability(Capabilities.RatingSubmit)]
    [ProducesResponseType(typeof(JeebRatingStatusEnvelope), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Submit([FromBody] JeebSubmitReviewRequest? body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        if (body is null || string.IsNullOrWhiteSpace(body.DeliveryId))
        {
            return BadRequest(Problem400("deliveryId is required."));
        }

        if (body.Score < MinStars || body.Score > MaxStars)
        {
            return BadRequest(Problem400($"score must be between {MinStars} and {MaxStars}."));
        }

        var parties = await ResolvePartiesAsync(body.DeliveryId, callerId, ct);
        if (parties.Result is not null) return parties.Result;
        var (callerRole, raterId, rateeId) = parties.Value;

        List<string> tags;
        try
        {
            tags = JeebRatingVocabulary.BuildTags(callerRole, body.Tags);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(Problem400(ex.Message));
        }

        var request = new SubmitBlindRatingRequest
        {
            CorrelationId = JeebRatingVocabulary.CorrelationForDelivery(body.DeliveryId),
            RaterId = raterId,
            RateeId = rateeId,
            Score = body.Score,
            Comment = body.Comment?.Trim(),
            Tags = tags,
        };

        try
        {
            var result = await _feedback.RatingsSubmitAsync(request, ct);
            return Ok(JeebReviewsProjection.ProjectStatus(body.DeliveryId.Trim(), result.State));
        }
        catch (FeedbackApiException ex)
        {
            return Problem(
                title: "Upstream feedback-service rejected the rating.",
                detail: ex.Message,
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// GET /v1/ratings/jeeb/{deliveryId}/status — the caller's reveal-state view of a
    /// delivery rating, shaped for the mobile <c>RatingStatus.fromJson</c> parser
    /// (<c>{ deliveryId, state, ratings, ratedCount }</c>). The counterparty stays
    /// hidden until both sides submit.
    /// </summary>
    [HttpGet("{deliveryId}/status")]
    [RequireCapability(Capabilities.DeliveryParticipate)]
    [ProducesResponseType(typeof(JeebRatingStatusEnvelope), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStatus(string deliveryId, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var callerId, out var unauthorized)) return unauthorized;

        var parties = await ResolvePartiesAsync(deliveryId, callerId, ct);
        if (parties.Result is not null) return parties.Result;
        var (_, raterId, _) = parties.Value;

        try
        {
            var correlationId = JeebRatingVocabulary.CorrelationForDelivery(deliveryId);
            var state = await _feedback.RatingsRevealAsync(correlationId, raterId, ct);
            return Ok(JeebReviewsProjection.ProjectStatus(deliveryId, state));
        }
        catch (FeedbackApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            // No rating recorded yet for this delivery → 404 (the mobile repo maps it
            // to notFound / an un-rated status, per DioRatingRepository).
            return NotFound();
        }
        catch (FeedbackApiException ex)
        {
            return Problem(
                title: "Upstream feedback-service rejected the reveal read.",
                detail: ex.Message,
                statusCode: ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway);
        }
    }

    /// <summary>
    /// Resolve the caller's Jeeb role + the rater/ratee GUIDs from the delivery
    /// parties — request-scoped, no stored state (ADR-0001). Mirrors
    /// <see cref="JeebRatingsController"/>'s party resolution.
    /// </summary>
    private async Task<(IActionResult? Result, (JeebRatingRole Role, Guid RaterId, Guid RateeId) Value)> ResolvePartiesAsync(
        string deliveryId,
        string callerId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deliveryId))
        {
            return (BadRequest(Problem400("deliveryId is required.")), default);
        }

        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null || string.IsNullOrEmpty(delivery.JeeberId))
        {
            return (NotFound(), default);
        }

        var callerIsClient = string.Equals(delivery.ClientId, callerId, StringComparison.Ordinal);
        var callerIsJeeber = string.Equals(delivery.JeeberId, callerId, StringComparison.Ordinal);
        if (!callerIsClient && !callerIsJeeber)
        {
            return (StatusCode(StatusCodes.Status403Forbidden, Problem403("You are not a party to this delivery.")), default);
        }

        if (!Guid.TryParse(callerId, out var raterId)
            || !Guid.TryParse(callerIsClient ? delivery.JeeberId : delivery.ClientId, out var rateeId))
        {
            return (BadRequest(Problem400("Delivery party identifiers are not valid GUIDs.")), default);
        }

        var role = JeebRatingVocabulary.RoleFor(callerIsClient);
        return (null, (role, raterId, rateeId));
    }

    private static ProblemDetails Problem400(string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status400BadRequest,
        Type = "https://jeeb.dev/errors/invalid-rating",
    };

    private static ProblemDetails Problem403(string title) => new()
    {
        Title = title,
        Status = StatusCodes.Status403Forbidden,
        Type = "https://jeeb.dev/errors/not-a-party",
    };
}

/// <summary>
/// POST /v1/ratings/jeeb/submit body — the mobile <c>DioRatingRepository</c> submit
/// shape (<c>{ deliveryId, raterId?, score, raterRole?, comment?, tags? }</c>). The
/// gateway resolves the authoritative rater/ratee from the delivery parties + bearer
/// token (the body's optional <c>raterId</c>/<c>raterRole</c> hints are ignored in
/// favour of the verified identity), then maps onto the generic opaque primitive.
/// </summary>
public sealed class JeebSubmitReviewRequest
{
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>The 0–5 star value (mobile sends <c>score</c>).</summary>
    public int Score { get; set; }

    public string? Comment { get; set; }

    /// <summary>Optional Jeeb tag taxonomy values (see <see cref="JeebRatingVocabulary.AllowedTags"/>).</summary>
    public List<string>? Tags { get; set; }
}
