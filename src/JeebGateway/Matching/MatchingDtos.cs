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

// The matching-service FastAPI read DTOs (MatchingServiceMatchesResponse /
// MatchingUsersResponse, mirroring GET /api/v1/matches/{user_id}) were REMOVED
// with the standalone matching-service read path (JEBV4-220 / E25, Q-020).
// Courier matching relocated to delivery-service; POST /matching/run (above)
// is the only surviving matching surface.
