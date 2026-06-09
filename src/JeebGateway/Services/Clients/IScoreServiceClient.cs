namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the SHARED, product-agnostic score-taking-service. The
/// mutual-blind reveal logic — the 7-day window, revealed_at, the per-delivery
/// pairing, and the jeeber/client vocabulary — lives ENTIRELY in the gateway
/// (see <see cref="JeebGateway.Ratings.BlindRevealPolicy"/> /
/// <see cref="JeebGateway.Ratings.RatingService"/>). This client only forwards
/// the canonical capture of ONE party's score to the downstream service via its
/// generic primitive (POST /api/scores).
///
/// <para>
/// JEB-1477 / Golden Rule 2 — the upstream contract is strictly generic:
/// <c>{ subjectId, authorId, subjectRole?, value(1-5), comment?, submittedAt } → scoreId</c>.
/// No Jeeb-domain identifiers (deliveryId, rateeUserId) or role literals
/// (jeeber/client) appear on this wire. The caller (RatingService) maps Jeeb
/// terms onto the generic shape: <c>deliveryId → subjectId</c> and the party
/// role (<c>jeeber</c>/<c>client</c>) → <c>subjectRole</c>.
/// </para>
///
/// <para>
/// AC9 / Golden Rule 4 — TRACKED DEBT: this is a hand-coded typed client because
/// score-taking-service does not yet publish a reachable OpenAPI document for the
/// generic capture route. Replace it with an NSwag-generated client (under
/// <c>src/JeebGateway/Services/Generated/</c>) once the upstream contract is
/// published; do not hand-roll a raw HttpClient. Throws
/// <see cref="HttpRequestException"/> on non-2xx.
/// </para>
/// </summary>
public interface IScoreServiceClient
{
    Task<SubmitScoreUpstreamResponse> SubmitScoreAsync(
        SubmitScoreUpstreamRequest request,
        CancellationToken ct);
}

/// <summary>
/// Generic upstream score-capture request. Product-agnostic by construction
/// (JEB-1477): every field is neutral so the shared service never learns any
/// Jeeb-domain vocabulary.
/// </summary>
public sealed class SubmitScoreUpstreamRequest
{
    /// <summary>Opaque identifier of the scored subject (gateway maps deliveryId → here).</summary>
    public required string SubjectId { get; init; }

    /// <summary>Opaque identifier of the party that authored the score.</summary>
    public required string AuthorId { get; init; }

    /// <summary>Optional caller-defined role of the subject (gateway maps the
    /// party role <c>jeeber</c>/<c>client</c> → here). No fixed vocabulary.</summary>
    public string? SubjectRole { get; init; }

    /// <summary>Score value, 1..5.</summary>
    public required int Value { get; init; }

    public string? Comment { get; init; }

    public required DateTimeOffset SubmittedAt { get; init; }
}

/// <summary>
/// Generic upstream score-capture response: <c>{ scoreId, subjectId, submittedAt }</c>.
/// </summary>
public sealed class SubmitScoreUpstreamResponse
{
    public required string ScoreId { get; init; }
    public required string SubjectId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
}
