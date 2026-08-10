using JeebGateway.Requests;
using JeebGateway.Tiers;

namespace JeebGateway.Geo;

/// <summary>Why a request is or is not offerable to a jeeber. Every value is a log reason.</summary>
public enum TierRadiusDecision
{
    Included,
    NoJeeberFix,
    NoPickupCoords,
    UnknownTier,
    OutOfRadius,
}

public readonly record struct TierRadiusEvaluation(
    TierRadiusDecision Decision,
    double? DistanceMeters)
{
    public bool IsIncluded => Decision == TierRadiusDecision.Included;
}

/// <summary>
/// Bug D2 — the fail-CLOSED jeeber/pickup distance cut, modelled on delivery-service
/// internal/matching/feed.go. Unknown distance excludes; there is no keep-on-empty fallback.
/// </summary>
public static class TierRadiusPolicy
{
    private const double EarthRadiusKm = 6371.0;

    public static TierRadiusEvaluation Evaluate(
        double? jeeberLat,
        double? jeeberLng,
        GeoPoint? pickup,
        DeliveryTier? tier)
    {
        if (jeeberLat is not { } jLat || jeeberLng is not { } jLng
            || double.IsNaN(jLat) || double.IsNaN(jLng)
            || jLat is < -90 or > 90 || jLng is < -180 or > 180)
        {
            return new(TierRadiusDecision.NoJeeberFix, null);
        }

        if (pickup is null || !pickup.IsValid())
        {
            return new(TierRadiusDecision.NoPickupCoords, null);
        }

        if (tier is null || tier.RadiusKm <= 0 || double.IsNaN(tier.RadiusKm))
        {
            return new(TierRadiusDecision.UnknownTier, null);
        }

        var km = HaversineKm(jLat, jLng, pickup.Lat, pickup.Lng);
        var metres = Math.Round(km * 1000.0, MidpointRounding.AwayFromZero);

        return km <= tier.RadiusKm
            ? new(TierRadiusDecision.Included, metres)
            : new(TierRadiusDecision.OutOfRadius, metres);
    }

    public static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        var dLat = ToRadians(lat2 - lat1);
        var dLng = ToRadians(lng2 - lng1);
        var a = (Math.Sin(dLat / 2) * Math.Sin(dLat / 2))
                + (Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2))
                   * Math.Sin(dLng / 2) * Math.Sin(dLng / 2));
        return EarthRadiusKm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    private static double ToRadians(double degrees) => degrees * Math.PI / 180.0;
}
