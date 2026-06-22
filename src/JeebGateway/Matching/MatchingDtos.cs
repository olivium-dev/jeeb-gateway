namespace JeebGateway.Matching;

/// <summary>
/// POST body for <c>/matching/run</c>. The caller hands in the pickup
/// point, the tier id (drives the radius), and an optional vehicle-type
/// allowlist. When <see cref="AllowedVehicleTypes"/> is null or empty
/// the engine treats every vehicle type as acceptable — the request is
/// just looking for any nearby online Jeeber.
/// </summary>
public sealed class MatchingRunRequest
{
    /// <summary>
    /// Existing delivery request id. When set, the engine looks up the
    /// row, validates ownership, and pulls pickup + tier from the row
    /// (the supplied fields, if any, are ignored). When null, the caller
    /// must supply <see cref="PickupLat"/>, <see cref="PickupLng"/>, and
    /// <see cref="TierId"/> directly — this dry-run shape powers the
    /// "what would I get?" preview UX before a request is created.
    /// </summary>
    public string? RequestId { get; set; }

    public double? PickupLat { get; set; }
    public double? PickupLng { get; set; }
    public string? TierId { get; set; }

    /// <summary>
    /// Optional vehicle allowlist (wire-format strings — "car", "motorbike",
    /// "bicycle", "scooter", "walk"). Unknown strings are rejected with 400.
    /// Empty or null means "any vehicle type".
    /// </summary>
    public List<string>? AllowedVehicleTypes { get; set; }
}

/// <summary>
/// Response shape for <c>/matching/run</c>. Clients render
/// <see cref="NotifiedCount"/> verbatim — "We notified N Jeebers near you".
/// The <see cref="Candidates"/> array is included for ops triage and to
/// drive the admin matching-debug view; mobile clients ignore it.
/// </summary>
public sealed class MatchingRunResponse
{
    public required string RequestId { get; init; }
    public required string TierId { get; init; }
    public required double RadiusKm { get; init; }
    public required int NotifiedCount { get; init; }
    public required int CandidateCount { get; init; }
    public required IReadOnlyList<MatchedJeeberDto> Candidates { get; init; }
    public required long ElapsedMs { get; init; }
}

public sealed class MatchedJeeberDto
{
    public required string UserId { get; init; }
    public required string VehicleType { get; init; }
    public required double DistanceKm { get; init; }
    public required double Rating { get; init; }
}

// ---------------------------------------------------------------------------
// DTOs for the real matching-service FastAPI endpoints
// (GET /api/v1/matches/{user_id} — app/api/endpoints/matches.py)
// ---------------------------------------------------------------------------

/// <summary>
/// Response shape returned by the matching-service's
/// <c>GET /api/v1/matches/{user_id}</c> endpoint.
/// <para>
/// Fields mirror the Python <c>MatchesResponse</c> Pydantic model.
/// </para>
/// </summary>
public sealed class MatchingServiceMatchesResponse
{
    /// <summary>Paginated list of matched user IDs.</summary>
    public List<string> Matches { get; init; } = [];

    /// <summary>Total number of matches before pagination.</summary>
    public int Total { get; init; }
}

/// <summary>
/// Gateway response shape for <c>GET /matching/users/{userId}</c>.
/// Wraps the upstream payload and adds the resolved user id for traceability.
/// </summary>
public sealed class MatchingUsersResponse
{
    public required string UserId { get; init; }
    public required List<string> Matches { get; init; }
    public required int Total { get; init; }
    public required int Skip { get; init; }
    public required int Limit { get; init; }
}

// ---------------------------------------------------------------------------
// iter6 B8: tier-aware find-jeebers + broadcast (matching-service FastAPI
// POST /api/v1/matching/find-jeebers). The mobile waiting screen (JM-025/026,
// DioOrderBroadcastService) calls the gateway BFF paths:
//   POST /v1/matching/find-jeebers  -> { count, jeeberIds, ... } (coverage)
//   POST /v1/matching/broadcast     -> 202 (fire-and-forget fan-out)
// Without these the request never goes live and the client UI shows "Expired".
// ---------------------------------------------------------------------------

/// <summary>
/// Wire shape posted to matching-service <c>POST /api/v1/matching/find-jeebers</c>.
/// The service requires exactly one tier identifier (<c>tier_id</c> OR
/// <c>tier_name</c>) plus an origin; <c>broadcast=true</c> publishes the matched
/// Jeeber list to the tier topic, <c>false</c> returns the candidate set only.
/// </summary>
public sealed class MatchingFindJeebersUpstreamRequest
{
    [System.Text.Json.Serialization.JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("tier_name")]
    public string? TierName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("origin_latitude")]
    public double OriginLatitude { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("origin_longitude")]
    public double OriginLongitude { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("broadcast")]
    public bool Broadcast { get; set; }
}

/// <summary>
/// Wire shape returned by matching-service <c>find-jeebers</c>
/// (mirrors the Python <c>FindJeebersResponse</c> Pydantic model — snake_case).
/// </summary>
public sealed class MatchingFindJeebersUpstreamResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("tier_name")]
    public string? TierName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("radius_km")]
    public double RadiusKm { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("jeeber_ids")]
    public List<string> JeeberIds { get; set; } = [];

    [System.Text.Json.Serialization.JsonPropertyName("match_count")]
    public int MatchCount { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("broadcast_topic")]
    public string? BroadcastTopic { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("broadcast_published")]
    public bool BroadcastPublished { get; set; }
}

/// <summary>
/// Gateway BFF result for <c>POST /v1/matching/find-jeebers</c>. The mobile
/// client reads <see cref="Count"/> verbatim for the coverage label; the other
/// fields drive the ops/no-coverage variants.
/// </summary>
public sealed class FindJeebersResponse
{
    public required int Count { get; init; }
    public required IReadOnlyList<string> JeeberIds { get; init; }
    public string? RequestId { get; init; }
    public string? Tier { get; init; }
    public double RadiusKm { get; init; }
    public bool BroadcastPublished { get; init; }
}
