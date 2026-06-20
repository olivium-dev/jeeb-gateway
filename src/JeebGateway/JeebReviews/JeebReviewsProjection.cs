using System;
using System.Collections.Generic;
using JeebGateway.service.ServiceFeedback;

namespace JeebGateway.JeebReviews;

/// <summary>
/// The generic→Jeeb projection for the reviews/ratings family routes the mobile app
/// consumes (<c>DioReviewsRepository</c> / <c>DioRatingRepository</c>):
///
/// <list type="bullet">
///   <item><c>GET /v1/ratings/jeeb/reviews</c> — per-jeeber reviews page.</item>
///   <item><c>POST /v1/ratings/jeeb/submit</c> — submit a rating (delegates the
///     opaque submit to the existing <c>JeebRatingVocabulary</c>/<c>ServiceFeedbackClient</c>).</item>
///   <item><c>GET /v1/ratings/jeeb/{deliveryId}/status</c> — the mobile-shaped
///     <c>{ deliveryId, state, ratings, ratedCount }</c> reveal view.</item>
/// </list>
///
/// <para>
/// ADR-0001 (STATELESS &amp; THIN): every method here is a pure, side-effect-free
/// shaping of the SHARED, product-agnostic feedback-service primitive into the
/// Jeeb-facing mobile contract — no state, no persistence, no domain rules. It is
/// the sibling of <see cref="JeebGateway.Ratings.Jeeb.JeebRatingProjection"/> and is
/// unit-tested without HTTP/DI.
/// </para>
///
/// <para>
/// Coverage note: the generic <see cref="ServiceFeedbackClient"/> exposes the
/// two-party blind-rating submit/reveal primitive and a per-tag AVERAGE read, but
/// NO per-jeeber reviews LIST and NO review moderation/report endpoint. Until those
/// generic reads exist, the reviews-list route returns the correctly-shaped
/// COLD-START EMPTY page the mobile <c>DioReviewsRepository._parse</c> tolerates
/// (D59 — averageScore null), and the report route accepts the request (202) rather
/// than fabricating moderation state in the (stateless) gateway. Re-point these to
/// the generic reads the moment feedback-service ships them — the mobile-facing
/// contract here does not change.
/// </para>
/// </summary>
public static class JeebReviewsProjection
{
    /// <summary>
    /// The mock score-taking-service status codes the mobile <c>RatingStatus</c>
    /// parser is authored against (it ALSO accepts the legacy
    /// <c>pending_mine</c>/<c>pending_theirs</c> codes the in-gateway path emits).
    /// </summary>
    public static class StatusCodes
    {
        public const string PendingBoth = "pending_both";
        public const string PendingSelf = "pending_self";
        public const string PendingCounter = "pending_counter";
        public const string Revealed = "revealed";
    }

    /// <summary>
    /// Build the correctly-shaped EMPTY, cold-start reviews page for a jeeber. This
    /// is the mobile-tolerated fallback used while the generic feedback-service has
    /// no per-jeeber reviews LIST read (see class remarks). D59: <c>coldStart</c> is
    /// true and <c>averageScore</c> is null so the mobile parser hides the aggregate
    /// and shows the "New" badge; the (empty) rows still render.
    /// </summary>
    public static JeebReviewsPageResponse EmptyReviewsPage(string jeeberId, int page, int pageSize)
    {
        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;
        return new JeebReviewsPageResponse
        {
            JeeberId = jeeberId ?? string.Empty,
            Items = Array.Empty<JeebReviewItemResponse>(),
            Page = safePage,
            PageSize = safeSize,
            TotalCount = 0,
            TotalPages = 1,
            ColdStart = true,
            ReviewCount = 0,
            AverageScore = null,
        };
    }

    /// <summary>
    /// Project the generic two-party reveal state into the mobile
    /// <c>{ deliveryId, state, ratings, ratedCount }</c> status envelope. The
    /// counterparty <c>ratings</c> row is only emitted once revealed (blind
    /// otherwise); the caller's own submitted row is always included once they have
    /// rated, so <c>ratedCount</c> reflects both parties.
    /// </summary>
    public static JeebRatingStatusEnvelope ProjectStatus(string deliveryId, BlindRevealStateResponse upstream)
    {
        if (upstream is null) throw new ArgumentNullException(nameof(upstream));

        var self = upstream.Self;
        var theirs = upstream.Counterparty;

        var selfSubmitted = self?.Submitted == true;
        var theirsSubmitted = theirs?.Submitted == true;

        var rows = new List<JeebRatingRow>();
        if (upstream.Revealed)
        {
            // Both submitted → counterparty detail is now visible.
            if (theirs is { Submitted: true })
            {
                rows.Add(new JeebRatingRow { Score = theirs.Score ?? 0, Comment = theirs.Comment });
            }
        }

        return new JeebRatingStatusEnvelope
        {
            DeliveryId = deliveryId,
            State = StateCode(upstream),
            Ratings = rows,
            RatedCount = (selfSubmitted ? 1 : 0) + (theirsSubmitted ? 1 : 0),
        };
    }

    /// <summary>
    /// Generic→Jeeb status mapping for the mobile reveal parser. Mirrors the
    /// in-gateway lattice (the shared primitive has no Jeeb 7-day window concept):
    /// <list type="bullet">
    ///   <item>both submitted → <c>revealed</c></item>
    ///   <item>only the viewer submitted → <c>pending_counter</c> (waiting on them)</item>
    ///   <item>only the counterparty submitted → <c>pending_self</c> (waiting on you)</item>
    ///   <item>neither submitted → <c>pending_both</c></item>
    /// </list>
    /// </summary>
    public static string StateCode(BlindRevealStateResponse upstream)
    {
        if (upstream.Revealed) return StatusCodes.Revealed;

        var selfSubmitted = upstream.Self?.Submitted == true;
        var theirsSubmitted = upstream.Counterparty?.Submitted == true;

        if (selfSubmitted) return StatusCodes.PendingCounter;   // you rated; waiting on them
        if (theirsSubmitted) return StatusCodes.PendingSelf;    // they rated; waiting on you
        return StatusCodes.PendingBoth;                         // nobody rated yet
    }
}
