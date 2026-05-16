namespace JeebGateway.Matching;

/// <summary>
/// In-memory stand-in for the PostGIS <c>ST_DWithin</c> radius check
/// (T-backend-008). The production matching engine pushes the radius
/// filter down to a GEOGRAPHY(Point, 4326) index in delivery-service;
/// the MVP gateway computes the great-circle distance with the
/// Haversine formula so the matching pipeline can run before the DB
/// migration that adds the spatial index lands.
///
/// Distances are returned in kilometres so the call sites can compare
/// directly against the tier's <c>RadiusKm</c> field without converting.
/// </summary>
internal static class Haversine
{
    private const double EarthRadiusKm = 6371.0088;

    /// <summary>
    /// Great-circle distance between two WGS84 points, in kilometres.
    /// </summary>
    public static double DistanceKm(double lat1, double lon1, double lat2, double lon2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLon = ToRadians(lon2 - lon1);
        var sinDLat = Math.Sin(dLat / 2);
        var sinDLon = Math.Sin(dLon / 2);
        var a = sinDLat * sinDLat
            + Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
            * sinDLon * sinDLon;
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        return EarthRadiusKm * c;
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
