using JeebGateway.Requests;

namespace JeebGateway.Tracking;

/// <summary>
/// MVP polyline builder. T-backend-014 calls for a straight-line route
/// from the Jeeber's current position to the delivery's dropoff so the
/// client UI has something to draw on the map; routing-service integration
/// (Mapbox / Google Directions) is the production follow-up.
/// </summary>
internal static class Polyline
{
    /// <summary>
    /// Returns an ordered [lat, lng] pair list from <paramref name="from"/>
    /// to <paramref name="to"/>, or an empty list when either endpoint is
    /// missing. Two-point straight line is enough for the MVP — the client
    /// renders it as a single polyline segment.
    /// </summary>
    public static IReadOnlyList<double[]> StraightLine(StoredPosition? from, GeoPoint? to)
    {
        if (from is null || to is null) return Array.Empty<double[]>();
        return new[]
        {
            new[] { from.Lat, from.Lng },
            new[] { to.Lat, to.Lng }
        };
    }
}
