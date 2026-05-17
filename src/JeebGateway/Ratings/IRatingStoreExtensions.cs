namespace JeebGateway.Ratings;

public sealed record UnrevealedRating(
    string Id,
    string DeliveryId,
    string RatedUserId,
    int Score,
    DateTimeOffset SubmittedAt);

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
    Task<IReadOnlyList<UnrevealedRating>> ListUnrevealedBeforeAsync(
        DateTimeOffset cutoff, CancellationToken ct);

    Task<bool> TryRevealAsync(string ratingId, DateTimeOffset at, CancellationToken ct);

    Task<IReadOnlyList<JeeberRatingSummary>> ListJeebersBelowAverageAsync(
        double threshold, int minRatings, CancellationToken ct);
}
