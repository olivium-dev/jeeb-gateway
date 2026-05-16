namespace JeebGateway.Matching;

/// <summary>
/// Geo-matching engine (T-backend-008). Given a pickup point, a tier
/// (whose <c>RadiusKm</c> drives the search ring), and a vehicle-type
/// allowlist, returns the ordered list of online Jeebers inside the
/// ring and fans out a "new offer" push to each one.
///
/// Production wiring swaps the in-memory scan for a PostGIS
/// <c>ST_DWithin</c> against the GEOGRAPHY(Point, 4326) column on
/// jeeber_availability; the MVP gateway computes the great-circle
/// distance with the Haversine formula so the matching pipeline can
/// run before the spatial index migration lands.
/// </summary>
public interface IMatchingService
{
    Task<MatchingOutcome> RunAsync(MatchingInput input, CancellationToken ct);
}
