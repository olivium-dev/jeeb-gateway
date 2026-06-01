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
