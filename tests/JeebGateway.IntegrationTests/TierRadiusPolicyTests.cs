using FluentAssertions;
using JeebGateway.Geo;
using JeebGateway.Requests;
using JeebGateway.Tiers;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Bug D2 — the fail-CLOSED tier-radius rule. Every "unknown" input must EXCLUDE; the live
/// defect was a ~9,000 km request shown to a 25 km jeeber with distanceMeters null.
/// </summary>
public sealed class TierRadiusPolicyTests
{
    // Amsterdam ↔ the exact pickup point from the D2 observation (~9,000 km apart).
    private const double JeeberLat = 52.3676;
    private const double JeeberLng = 4.9041;
    private const double FarPickupLat = 39.237255;
    private const double FarPickupLng = -123.1500317;

    private static DeliveryTier Tier(string id, double radiusKm) => new()
    {
        Id = id,
        Name = id,
        SlaHours = 1,
        RadiusKm = radiusKm,
        RequestTtlSeconds = 60,
        CommissionRate = 0.1,
        PriceHint = "hint",
    };

    private static GeoPoint Point(double lat, double lng) => new() { Lat = lat, Lng = lng };

    [Fact]
    public void The_D2_case_is_out_of_radius_on_the_25km_tier()
    {
        var result = TierRadiusPolicy.Evaluate(
            JeeberLat, JeeberLng, Point(FarPickupLat, FarPickupLng), Tier("scheduled", 25.0));

        result.Decision.Should().Be(TierRadiusDecision.OutOfRadius);
        result.IsIncluded.Should().BeFalse();
        result.DistanceMeters.Should().BeGreaterThan(8_000_000,
            "the observed D2 pair is roughly 9,000 km apart, so the distance is computed, "
            + "not null — a null distance was the symptom that the cut never ran");
    }

    [Theory]
    [InlineData(3.0)]
    [InlineData(10.0)]
    [InlineData(25.0)]
    public void A_pickup_inside_the_tier_radius_is_included_with_a_real_distance(double radiusKm)
    {
        // ~1.1 km north of the jeeber: inside all three seeded radii (3 / 10 / 25 km).
        var result = TierRadiusPolicy.Evaluate(
            JeeberLat, JeeberLng, Point(JeeberLat + 0.01, JeeberLng), Tier("t", radiusKm));

        result.Decision.Should().Be(TierRadiusDecision.Included);
        result.DistanceMeters.Should().NotBeNull().And.BeInRange(1_000, 1_300);
    }

    [Fact]
    public void A_jeeber_with_no_fix_is_excluded_not_admitted()
    {
        TierRadiusPolicy.Evaluate(null, null, Point(JeeberLat, JeeberLng), Tier("t", 25.0))
            .Decision.Should().Be(TierRadiusDecision.NoJeeberFix);

        TierRadiusPolicy.Evaluate(JeeberLat, null, Point(JeeberLat, JeeberLng), Tier("t", 25.0))
            .Decision.Should().Be(TierRadiusDecision.NoJeeberFix);
    }

    [Fact]
    public void A_request_with_no_pickup_point_is_excluded()
        => TierRadiusPolicy.Evaluate(JeeberLat, JeeberLng, null, Tier("t", 25.0))
            .Decision.Should().Be(TierRadiusDecision.NoPickupCoords);

    [Fact]
    public void An_out_of_range_pickup_point_is_treated_as_absent()
        => TierRadiusPolicy.Evaluate(JeeberLat, JeeberLng, Point(999, 999), Tier("t", 25.0))
            .Decision.Should().Be(TierRadiusDecision.NoPickupCoords);

    [Fact]
    public void An_unknown_tier_excludes_rather_than_defaulting_to_a_radius()
        => TierRadiusPolicy.Evaluate(JeeberLat, JeeberLng, Point(JeeberLat, JeeberLng), null)
            .Decision.Should().Be(TierRadiusDecision.UnknownTier);

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void A_non_positive_radius_excludes(double radiusKm)
        => TierRadiusPolicy.Evaluate(
                JeeberLat, JeeberLng, Point(JeeberLat, JeeberLng), Tier("t", radiusKm))
            .Decision.Should().Be(TierRadiusDecision.UnknownTier);

    [Fact]
    public void An_included_result_always_carries_a_distance()
    {
        // The mobile/E2E invariant: a listed feed item can never have distanceMeters null.
        var result = TierRadiusPolicy.Evaluate(
            JeeberLat, JeeberLng, Point(JeeberLat, JeeberLng), Tier("t", 3.0));

        result.IsIncluded.Should().BeTrue();
        result.DistanceMeters.Should().NotBeNull();
    }

    [Fact]
    public void Haversine_matches_a_known_great_circle_distance()
        // Amsterdam → Paris is ~430 km; pins the maths so a unit slip (m vs km) is caught.
        => TierRadiusPolicy.HaversineKm(JeeberLat, JeeberLng, 48.8566, 2.3522)
            .Should().BeInRange(420, 440);

    // ── diagnostics: an unknown tier must not MASK the other exclusion facts ──

    [Fact]
    public void An_unknown_tier_still_reports_the_distance_it_could_compute()
    {
        // Pre-fix the tier rung short-circuited before the haversine, so every UnknownTier
        // exclusion logged distanceMeters=null — fixing the catalog was the only way to find
        // out the row was ALSO thousands of km out of range.
        var result = TierRadiusPolicy.Evaluate(JeeberLat, JeeberLng, Point(48.8566, 2.3522), null);

        result.Decision.Should().Be(TierRadiusDecision.UnknownTier);
        result.DistanceMeters.Should().NotBeNull().And.BeGreaterThan(400_000);
        result.RadiusKm.Should().BeNull();
    }

    [Fact]
    public void An_unreadable_catalog_is_a_distinct_reason_from_an_unknown_tier()
    {
        TierRadiusPolicy
            .Evaluate(JeeberLat, JeeberLng, Point(JeeberLat, JeeberLng), null,
                tierCatalogAvailable: false)
            .Decision.Should().Be(TierRadiusDecision.TierCatalogUnavailable);

        TierRadiusPolicy
            .Evaluate(JeeberLat, JeeberLng, Point(JeeberLat, JeeberLng), null,
                tierCatalogAvailable: true)
            .Decision.Should().Be(TierRadiusDecision.UnknownTier);
    }

    [Fact]
    public void A_geometry_gap_is_reported_before_the_tier_and_still_carries_the_radius()
    {
        // Ordering: a missing fix is the primary reason, but the radius the decision WOULD have
        // used travels with it so one log line explains the whole decision.
        var result = TierRadiusPolicy.Evaluate(null, null, Point(JeeberLat, JeeberLng), Tier("t", 25.0));

        result.Decision.Should().Be(TierRadiusDecision.NoJeeberFix);
        result.RadiusKm.Should().Be(25.0);
    }

    [Theory]
    [InlineData(1.0, TierRadiusDecision.OutOfRadius)]
    [InlineData(500.0, TierRadiusDecision.Included)]
    public void Every_tier_backed_decision_carries_both_numbers(
        double radiusKm, TierRadiusDecision expected)
    {
        var result = TierRadiusPolicy.Evaluate(
            JeeberLat, JeeberLng, Point(JeeberLat + 0.05, JeeberLng), Tier("t", radiusKm));

        result.Decision.Should().Be(expected);
        result.RadiusKm.Should().Be(radiusKm);
        result.DistanceMeters.Should().NotBeNull();
    }
}
