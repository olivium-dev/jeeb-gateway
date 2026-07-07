namespace JeebGateway.Ratings;

public sealed record RatingWindowSweepResult(int RevealedCount, int ClosedCount);

public sealed record JeeberRatingSummary(
    string JeeberId,
    double AverageScore,
    int RatingCount);

/// <summary>
/// Extension store contract for the reveal job (T-backend-021) and
/// auto-flag job (T-backend-040). Kept separate from <see cref="IRatingStore"/>
/// so the base contract remains stable.
/// </summary>
public interface IRatingStoreExtended : IRatingStore
{
    Task<RatingWindowSweepResult> SweepExpiredWindowsAsync(
        DateTimeOffset deliveredAtCutoff, DateTimeOffset processedAt, CancellationToken ct);

    Task<IReadOnlyList<JeeberRatingSummary>> ListJeebersBelowAverageAsync(
        double threshold, int minRatings, CancellationToken ct);
}
