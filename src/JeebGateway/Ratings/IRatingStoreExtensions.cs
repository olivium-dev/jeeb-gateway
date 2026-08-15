namespace JeebGateway.Ratings;

public sealed record RatingWindowSweepResult(int RevealedCount, int ClosedCount);

/// <summary>
/// Extension store contract kept separate from <see cref="IRatingStore"/>
/// so the base contract remains stable.
/// </summary>
public interface IRatingStoreExtended : IRatingStore
{
    Task<RatingWindowSweepResult> SweepExpiredWindowsAsync(
        DateTimeOffset deliveredAtCutoff, DateTimeOffset processedAt, CancellationToken ct);
}
