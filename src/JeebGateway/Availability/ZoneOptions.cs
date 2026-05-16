namespace JeebGateway.Availability;

/// <summary>
/// Configurable zone boundaries used by the admin operations map
/// (T-backend-051). Each <see cref="ZoneBoundary"/> is an axis-aligned
/// WGS84 bounding box; an online Jeeber is grouped into the first zone
/// whose box contains their last-known location. Jeebers with no
/// location or whose location falls outside every configured zone are
/// bucketed under <see cref="UnzonedKey"/>.
///
/// Bounding boxes are intentionally simpler than the GIS-grade polygons
/// that <c>jeeber_availability.last_location</c> can hold — the ops map
/// only needs an at-a-glance grouping, and rectangles keep the config
/// (and the matching logic) trivial to reason about.
/// </summary>
public class ZoneOptions
{
    public const string SectionName = "Admin:Zones";

    /// <summary>
    /// Bucket key used for online Jeebers whose location falls outside
    /// every configured zone (or who have no location reported yet).
    /// </summary>
    public const string UnzonedKey = "unzoned";

    public List<ZoneBoundary> Boundaries { get; set; } = new();

    /// <summary>
    /// Cache lifetime advertised to admin clients. Matches the 30-second
    /// polling cadence in the T-backend-051 acceptance criteria.
    /// </summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);
}

public class ZoneBoundary
{
    public required string Key { get; set; }
    public string? Name { get; set; }
    public required double MinLatitude { get; set; }
    public required double MaxLatitude { get; set; }
    public required double MinLongitude { get; set; }
    public required double MaxLongitude { get; set; }

    public bool Contains(double latitude, double longitude) =>
        latitude >= MinLatitude && latitude <= MaxLatitude
        && longitude >= MinLongitude && longitude <= MaxLongitude;
}
