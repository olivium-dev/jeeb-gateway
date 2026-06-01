namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real <c>feedback-service</c> (.NET 8 / Swashbuckle,
/// live at <c>http://192.168.2.50:10064</c>, host port 10064, liveness-only —
/// no <c>/health</c> readiness route). Hand-coded against the published
/// Swashbuckle spec at <c>/swagger/v1/swagger.json</c> (title "Feedback Service
/// API", paths <c>POST/GET/DELETE /Review/comment</c>,
/// <c>GET /Review/comment/grouped</c>, <c>GET /Review/rating</c>) following the
/// <see cref="INotificationServiceClient"/> precedent — a typed hand-coded
/// client rather than the NSwag-generated artifact, because the rating module
/// only needs the submit + read seam and the generated client would be heavier
/// than the surface we consume.
///
/// <para>
/// SCORE-TAKING RESOLUTION (read this before touching the rating wiring):
/// the gateway historically shipped an <see cref="IScoreServiceClient"/>
/// targeting <c>Services:ScoreTaking:BaseUrl</c> → <c>POST /api/scores</c>.
/// That <c>score-taking-service</c> has NO appsettings entry in any environment
/// (only a unit-test fake at <c>http://score.test</c>) and is NOT in the
/// deployed fleet — it is stale/aspirational. The REAL canonical ratings
/// upstream is this feedback-service. <see cref="IRatingService"/> therefore
/// routes its record-of-truth here when <c>FeatureFlags:UseUpstream:Feedback</c>
/// is on, and keeps the in-memory <see cref="JeebGateway.Ratings.IRatingStore"/>
/// as the off/fallback path. <see cref="IScoreServiceClient"/> is left in place
/// (still referenced by the typed-client pipeline test) but is no longer the
/// rating record-of-truth.
/// </para>
///
/// <para>
/// CONTRACT MAPPING. feedback-service models reviews as comments scoped by a
/// free-form <c>tag</c> (the "topic" — for Jeeb ratings we use the ratee user
/// id) plus a <c>criteria</c> bucket and a 1..5 <c>rating</c>. The mutual-blind
/// reveal logic and the per-delivery pairing stay in the gateway
/// (<see cref="JeebGateway.Ratings.BlindRevealPolicy"/> /
/// <see cref="JeebGateway.Ratings.IRatingStore"/>); this client only persists
/// the one-party rating into the canonical store and reads the aggregate back.
/// </para>
///
/// The named "feedback" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:Feedback:BaseUrl</c>) + the org-standard resilience
/// pipeline; this class never thinks about retry/timeout/circuit-breaker.
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IFeedbackServiceClient
{
    /// <summary>
    /// Persists a single party's rating as a comment in feedback-service via
    /// <c>POST /Review/comment</c>. Maps the gateway's submit to a
    /// <c>CreateCommentRequest</c>: <paramref name="request"/>.RateeUserId →
    /// <c>tag</c> (the review topic), AuthorUserId → <c>commenterId</c>,
    /// Stars → <c>rating</c>, the author role → <c>criteria</c>, Comment →
    /// <c>text</c>.
    /// </summary>
    Task<FeedbackCommentResponse> SubmitCommentAsync(
        FeedbackSubmitRequest request,
        CancellationToken ct);

    /// <summary>
    /// Reads the aggregate average rating for a topic via
    /// <c>GET /Review/rating?tag=...</c>. Returns <c>null</c> when the topic has
    /// no reviews yet (upstream returns <c>{ "averageRating": null }</c>).
    /// </summary>
    Task<double?> GetAverageRatingAsync(
        string tag,
        CancellationToken ct);

    /// <summary>
    /// Reads the comment list + distribution for a topic via
    /// <c>GET /Review/comment?Tag=...&amp;Length=...&amp;Offset=...</c>. This is
    /// the tested read path that reaches the feedback-service database.
    /// </summary>
    Task<FeedbackCommentsPage> ListCommentsAsync(
        string tag,
        int length,
        int offset,
        CancellationToken ct);
}

/// <summary>
/// Gateway-side submit input. The gateway resolves the per-delivery pairing and
/// blind-reveal projection itself; this carries only what feedback-service needs
/// to persist one party's rating.
/// </summary>
public sealed class FeedbackSubmitRequest
{
    /// <summary>The review topic — the ratee user id for Jeeb ratings.</summary>
    public required string Tag { get; init; }

    /// <summary>The author of the rating — maps to <c>commenterId</c> (must be a GUID upstream).</summary>
    public required string CommenterId { get; init; }

    /// <summary>1..5 stars.</summary>
    public required int Rating { get; init; }

    /// <summary>The criteria bucket — Jeeb uses the author role (<c>client</c>/<c>jeeber</c>).</summary>
    public required string Criteria { get; init; }

    /// <summary>Optional free-text comment (max 1000 chars upstream).</summary>
    public string? Text { get; init; }

    /// <summary>Optional review title (max 200 chars upstream).</summary>
    public string? ReviewTitle { get; init; }
}

/// <summary>Mirror of feedback-service's <c>CommentResponse</c>.</summary>
public sealed class FeedbackCommentResponse
{
    public string? Id { get; init; }
    public string? CommenterId { get; init; }
    public int Rating { get; init; }
    public string? TopicId { get; init; }
    public string? Text { get; init; }
    public DateTimeOffset Date { get; init; }
    public string? Tag { get; init; }
    public string? Criteria { get; init; }
    public string? ReviewTitle { get; init; }
}

/// <summary>Mirror of feedback-service's <c>GetCommentsResponse</c> envelope.</summary>
public sealed class FeedbackCommentsPage
{
    public IReadOnlyList<FeedbackCommentResponse> Comments { get; init; } =
        Array.Empty<FeedbackCommentResponse>();
    public int TotalReviewCount { get; init; }
    public double AverageRating { get; init; }
}
