using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace JeebGateway.JeebReviews;

/// <summary>
/// One review row for <c>GET /v1/ratings/jeeb/reviews</c>. Keys match the exact
/// camelCase the mobile <c>DioReviewsRepository._review</c> reads (it also
/// tolerates snake_case + a legacy <c>rating</c> alias for <c>score</c>, but the
/// gateway emits the canonical R1m camelCase shape).
/// </summary>
public sealed class JeebReviewItemResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    /// <summary>D58 — the reviewer's FIRST NAME only (never a full name).</summary>
    [JsonPropertyName("reviewerFirstName")]
    public string ReviewerFirstName { get; set; } = string.Empty;

    /// <summary>The 0–5 star value (R1m <c>score</c>).</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>The free-text review comment (R1m <c>body</c>); null for a star-only rating.</summary>
    [JsonPropertyName("body")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Body { get; set; }

    /// <summary>ISO-8601 timestamp (R1m <c>createdAt</c>).</summary>
    [JsonPropertyName("createdAt")]
    public string CreatedAt { get; set; } = string.Empty;

    /// <summary>D27 — per-row report affordance. R1m returns true for every row.</summary>
    [JsonPropertyName("reportable")]
    public bool Reportable { get; set; } = true;
}

/// <summary>
/// The paginated per-jeeber reviews envelope for <c>GET /v1/ratings/jeeb/reviews</c>.
/// Keys match the R1m wire shape the mobile <c>DioReviewsRepository._parse</c>
/// reads: <c>{ jeeberId, items, page, pageSize, totalCount, totalPages,
/// coldStart, reviewCount, averageScore }</c>. D59: while <c>coldStart</c> the
/// aggregate <c>averageScore</c> is null (the mobile parser hides it).
/// </summary>
public sealed class JeebReviewsPageResponse
{
    [JsonPropertyName("jeeberId")]
    public string JeeberId { get; set; } = string.Empty;

    [JsonPropertyName("items")]
    public IReadOnlyList<JeebReviewItemResponse> Items { get; set; } = new List<JeebReviewItemResponse>();

    [JsonPropertyName("page")]
    public int Page { get; set; }

    [JsonPropertyName("pageSize")]
    public int PageSize { get; set; }

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    /// <summary>D59 — jeeber has &lt; 5 ratings: mobile hides the aggregate + shows the New badge.</summary>
    [JsonPropertyName("coldStart")]
    public bool ColdStart { get; set; }

    /// <summary>The jeeber's total rating count (R1m <c>reviewCount</c>).</summary>
    [JsonPropertyName("reviewCount")]
    public int ReviewCount { get; set; }

    /// <summary>The aggregate score, or null while <c>coldStart</c> (D59 hidden). Omitted when null.</summary>
    [JsonPropertyName("averageScore")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? AverageScore { get; set; }
}

/// <summary>
/// One counterparty/own rating row inside <see cref="JeebRatingStatusEnvelope"/>.
/// Mobile <c>RatingStatus._parseCounterpart</c> reads a <c>ratings:[...]</c> list
/// and takes its first element's <c>score</c> + <c>comment</c>.
/// </summary>
public sealed class JeebRatingRow
{
    /// <summary>The star value (mobile reads <c>score</c>, tolerates a legacy <c>stars</c>).</summary>
    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("comment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Comment { get; set; }
}

/// <summary>
/// The status body for <c>GET /v1/ratings/jeeb/{deliveryId}/status</c>. Keys match
/// the exact shape the mobile <c>RatingStatus.fromJson</c> reads:
/// <c>{ deliveryId, state, ratings:[{score,comment}], ratedCount }</c>. <c>state</c>
/// is one of <c>pending_both | pending_self | pending_counter | revealed</c> (the
/// mobile parser also accepts the legacy <c>pending_mine</c>/<c>pending_theirs</c>
/// codes). The counterparty <c>ratings</c> row is only populated once revealed
/// (blind until both submit).
/// </summary>
public sealed class JeebRatingStatusEnvelope
{
    [JsonPropertyName("deliveryId")]
    public string DeliveryId { get; set; } = string.Empty;

    /// <summary>pending_both | pending_self | pending_counter | revealed.</summary>
    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    /// <summary>Revealed counterparty rating rows; empty while blind.</summary>
    [JsonPropertyName("ratings")]
    public IReadOnlyList<JeebRatingRow> Ratings { get; set; } = new List<JeebRatingRow>();

    /// <summary>How many of the two parties have submitted (0, 1, or 2).</summary>
    [JsonPropertyName("ratedCount")]
    public int RatedCount { get; set; }
}
