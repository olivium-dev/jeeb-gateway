namespace JeebGateway.Matching;

/// <summary>
/// Read-only source of per-Jeeber rating used as the secondary sort key
/// by the matching engine (T-backend-008). Production wiring will resolve
/// the rolling average from the ratings service (FR-7); the MVP swap is
/// an in-memory store seeded by tests.
///
/// Returns 0 for unrated Jeebers so they consistently sink to the bottom
/// of the proximity-tied bucket — a brand-new Jeeber with no rating must
/// not jump ahead of a high-rated one at the same distance.
/// </summary>
public interface IJeeberRatingProvider
{
    Task<double> GetRatingAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Bulk variant — the matching engine resolves ratings for every
    /// candidate in a single call so a Postgres-backed implementation
    /// can replace per-row lookups with a single WHERE id = ANY(...) query.
    /// </summary>
    Task<IReadOnlyDictionary<string, double>> GetRatingsAsync(
        IReadOnlyCollection<string> userIds,
        CancellationToken ct);
}
