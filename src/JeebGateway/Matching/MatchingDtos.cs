using JeebGateway.Availability;

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

/// <summary>
/// In-process input handed to <see cref="IMatchingService"/>. Mirrors
/// <see cref="MatchingRunRequest"/> after the controller resolves the
/// optional <c>RequestId</c> path into pickup + tier values.
/// </summary>
public sealed class MatchingInput
{
    public required string RequestId { get; init; }
    public required double PickupLat { get; init; }
    public required double PickupLng { get; init; }
    public required string TierId { get; init; }
    public required IReadOnlySet<VehicleType> AllowedVehicleTypes { get; init; }
}

/// <summary>
/// In-process output of <see cref="IMatchingService.RunAsync"/>. Holds
/// the ordered candidate list plus the radius used so the controller can
/// echo both back to the caller.
/// </summary>
public sealed class MatchingOutcome
{
    public required string RequestId { get; init; }
    public required string TierId { get; init; }
    public required double RadiusKm { get; init; }
    public required int NotifiedCount { get; init; }
    public required IReadOnlyList<MatchedJeeber> Candidates { get; init; }
    public required long ElapsedMs { get; init; }
}

public sealed class MatchedJeeber
{
    public required string UserId { get; init; }
    public required VehicleType VehicleType { get; init; }
    public required double DistanceKm { get; init; }
    public required double Rating { get; init; }
}
