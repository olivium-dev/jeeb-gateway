namespace JeebGateway.Availability;

/// <summary>
/// Top-level payload for the admin operations map (T-backend-051).
/// One entry per configured zone plus a synthetic <see cref="ZoneOptions.UnzonedKey"/>
/// bucket for Jeebers outside every boundary.
/// </summary>
public class AdminZoneViewResponse
{
    public required IReadOnlyList<AdminZoneGroup> Zones { get; init; }
    public required int TotalOnline { get; init; }
    public required DateTimeOffset GeneratedAt { get; init; }
    public required int RefreshIntervalSeconds { get; init; }
}

public class AdminZoneGroup
{
    public required string Key { get; init; }
    public string? Name { get; init; }
    public ZoneBoundsDto? Bounds { get; init; }
    public required int Count { get; init; }
    public required IReadOnlyDictionary<string, int> CountByVehicleType { get; init; }
    public required IReadOnlyList<AdminJeeberMarker> Jeebers { get; init; }
}

public class ZoneBoundsDto
{
    public required double MinLatitude { get; init; }
    public required double MaxLatitude { get; init; }
    public required double MinLongitude { get; init; }
    public required double MaxLongitude { get; init; }
}

public class AdminJeeberMarker
{
    public required string UserId { get; init; }
    public required string VehicleType { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
}
