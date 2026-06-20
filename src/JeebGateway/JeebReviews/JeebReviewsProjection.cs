using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>
        /// The system auto-revealed the ratings after the time window elapsed without
        /// both parties submitting. Fires when <c>jeeb.rating_auto_revealed</c> is
        /// delivered. Detected by: upstream <c>revealed == true</c> AND
        /// <c>submittedCount &lt; 2</c> (only one side rated before the window closed).
        /// </summary>
        public const string AutoRevealed = "auto-revealed";
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
    /// D59 cold-start threshold: a jeeber with fewer than this many revealed reviews is
    /// "new" — the mobile parser hides the aggregate score and shows the "New" badge.
    /// </summary>
    public const int ColdStartReviewThreshold = 5;

    /// <summary>
    /// Project the generic, opaque per-ratee reviews page (the SHARED feedback-service
    /// list-by-ratee read) into the Jeeb-facing R1m page the mobile
    /// <c>DioReviewsRepository._parse</c> consumes. Pure shaping — no state, no HTTP.
    ///
    /// <para>The upstream is product-agnostic: opaque rater/ratee ids + score + comment.
    /// The reviewer's name is NOT stored downstream (the shared service must not know Jeeb
    /// identities — Golden Rule 2), so the row's <c>reviewerFirstName</c> is left empty;
    /// the mobile parser renders an anonymous reviewer when it is blank (D58 — never a full
    /// name). Score + comment + timestamp + the aggregate count/average are real, DB-backed
    /// values from the revealed blind ratings. D59: while the jeeber has &lt;
    /// <see cref="ColdStartReviewThreshold"/> reviews the aggregate <c>averageScore</c> is
    /// suppressed (null) and <c>coldStart</c> is true.</para>
    /// </summary>
    public static JeebReviewsPageResponse ProjectReviewsPage(
        string jeeberId, RateeReviewsResponse upstream, int page, int pageSize)
    {
        if (upstream is null) return EmptyReviewsPage(jeeberId, page, pageSize);

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;

        var items = (upstream.Reviews ?? new List<RateeReviewItem>())
            .Select(r => new JeebReviewItemResponse
            {
                Id = r.Id.ToString(),
                ReviewerFirstName = string.Empty, // opaque upstream — no identity (D58)
                Score = r.Score,
                Body = string.IsNullOrWhiteSpace(r.Comment) ? null : r.Comment,
                CreatedAt = (r.RevealedAt ?? r.CreatedAt).ToString("o"),
                Reportable = true, // D27
            })
            .ToList();

        var total = upstream.TotalReviewCount;
        var coldStart = total < ColdStartReviewThreshold;
        var totalPages = total <= 0 ? 1 : (int)Math.Ceiling(total / (double)safeSize);

        return new JeebReviewsPageResponse
        {
            JeeberId = jeeberId ?? string.Empty,
            Items = items,
            Page = safePage,
            PageSize = safeSize,
            TotalCount = total,
            TotalPages = totalPages < 1 ? 1 : totalPages,
            ColdStart = coldStart,
            ReviewCount = total,
            // D59 — suppress the aggregate until past the cold-start threshold.
            AverageScore = coldStart ? null : Math.Round(upstream.AverageRating, 2),
        };
    }

    /// <summary>
    /// REALAPP fix — project the generic feedback-service per-tag REVIEW page
    /// (<c>GET /Review/comment?Tag=&amp;Length=&amp;Offset=&amp;Filter=</c> →
    /// <see cref="GetCommentsResponse"/>: <c>{ comments[], totalReviewCount,
    /// averageRating }</c>) into the Jeeb-facing R1m page the mobile
    /// <c>DioReviewsRepository._parse</c> consumes. Pure shaping — no state, no HTTP.
    ///
    /// <para>This is the read surface the jeeber-review WRITE side stamps with the
    /// jeeber's id as the public review TAG, so the rows are real, DB-backed reviews.
    /// Each <see cref="CommentResponse"/> carries the rating (score), free text, the
    /// review date and an opaque <c>commenterId</c>; the reviewer's NAME is not stored
    /// downstream (the shared service must not know Jeeb identities — GR2), so
    /// <c>reviewerFirstName</c> is left empty and the mobile parser renders an
    /// anonymous reviewer (D58 — never a full name). D59: while the jeeber has fewer
    /// than <see cref="ColdStartReviewThreshold"/> reviews the aggregate
    /// <c>averageScore</c> is suppressed (null) and <c>coldStart</c> is true.</para>
    /// </summary>
    public static JeebReviewsPageResponse ProjectCommentsPage(
        string jeeberId, GetCommentsResponse upstream, int page, int pageSize)
    {
        if (upstream is null) return EmptyReviewsPage(jeeberId, page, pageSize);

        var safePage = page < 1 ? 1 : page;
        var safeSize = pageSize < 1 ? 20 : pageSize;

        var items = (upstream.Comments ?? new List<CommentResponse>())
            .Select(c => new JeebReviewItemResponse
            {
                Id = c.Id.ToString(),
                ReviewerFirstName = string.Empty, // opaque upstream — no identity (D58)
                Score = c.Rating,
                Body = string.IsNullOrWhiteSpace(c.Text) ? null : c.Text,
                CreatedAt = c.Date.ToString("o"),
                Reportable = true, // D27
            })
            .ToList();

        var total = upstream.TotalReviewCount;
        var coldStart = total < ColdStartReviewThreshold;
        var totalPages = total <= 0 ? 1 : (int)Math.Ceiling(total / (double)safeSize);

        return new JeebReviewsPageResponse
        {
            JeeberId = jeeberId ?? string.Empty,
            Items = items,
            Page = safePage,
            PageSize = safeSize,
            TotalCount = total,
            TotalPages = totalPages < 1 ? 1 : totalPages,
            ColdStart = coldStart,
            ReviewCount = total,
            // D59 — suppress the aggregate until past the cold-start threshold.
            AverageScore = coldStart ? null : Math.Round(upstream.AverageRating, 2),
        };
    }

    /// <summary>
    /// Project the generic two-party reveal state into the mobile
    /// <c>{ deliveryId, state, ratings, ratedCount }</c> status envelope. The
    /// counterparty <c>ratings</c> row is only emitted once revealed (blind
    /// otherwise); the caller's own submitted row is always included once they have
    /// rated, so <c>ratedCount</c> reflects both parties.
    ///
    /// <para>
    /// When the upstream is revealed with fewer than 2 submissions (auto-reveal),
    /// <c>state</c> is <c>auto-revealed</c> and any counterparty rating that WAS
    /// submitted before the window closed is still included in <c>ratings</c>.
    /// </para>
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
            // Revealed (mutual or auto) → expose any counterparty rating that was submitted.
            // For auto-revealed the counterparty may not have rated, so the guard is required.
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
    ///   <item>both submitted AND revealed → <c>revealed</c></item>
    ///   <item>revealed but fewer than 2 submitted → <c>auto-revealed</c> (time-window
    ///     expiry: one side — or neither — rated before the window closed and the system
    ///     auto-revealed; this is the server-side trigger for the
    ///     <c>jeeb.rating_auto_revealed</c> notification)</item>
    ///   <item>only the viewer submitted → <c>pending_counter</c> (waiting on them)</item>
    ///   <item>only the counterparty submitted → <c>pending_self</c> (waiting on you)</item>
    ///   <item>neither submitted → <c>pending_both</c></item>
    /// </list>
    /// </summary>
    public static string StateCode(BlindRevealStateResponse upstream)
    {
        if (upstream.Revealed)
        {
            // Auto-reveal fires when the time window expires with fewer than 2 submissions.
            // SubmittedCount < 2 means the system revealed unilaterally (one-sided or empty).
            if (upstream.SubmittedCount < 2) return StatusCodes.AutoRevealed;
            return StatusCodes.Revealed;
        }

        var selfSubmitted = upstream.Self?.Submitted == true;
        var theirsSubmitted = upstream.Counterparty?.Submitted == true;

        if (selfSubmitted) return StatusCodes.PendingCounter;   // you rated; waiting on them
        if (theirsSubmitted) return StatusCodes.PendingSelf;    // they rated; waiting on you
        return StatusCodes.PendingBoth;                         // nobody rated yet
    }
}
