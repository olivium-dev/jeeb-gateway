using Microsoft.Extensions.Logging;

namespace JeebGateway.Ratings;

/// <summary>
/// Explicit fail-closed extended rating store for the feedback-service-backed path.
/// The current <c>ServiceFeedbackClient</c> blind-rating surface exposes submit and
/// per-correlation reveal reads only; it does not expose the required upstream
/// capabilities to list expired-but-unrevealed windows or mark them revealed/closed.
/// </summary>
public sealed class UnsupportedUpstreamRatingStoreExtended : IRatingStoreExtended
{
    private readonly FeedbackServiceRatingStore _inner;
    private readonly ILogger<UnsupportedUpstreamRatingStoreExtended> _logger;

    public UnsupportedUpstreamRatingStoreExtended(
        FeedbackServiceRatingStore inner,
        ILogger<UnsupportedUpstreamRatingStoreExtended> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public Task<RatingPair?> GetAsync(string deliveryId, CancellationToken ct) =>
        _inner.GetAsync(deliveryId, ct);

    public Task<RatingPair> EnsureAsync(
        string deliveryId,
        string clientId,
        string jeeberId,
        DateTimeOffset deliveredAt,
        CancellationToken ct) =>
        _inner.EnsureAsync(deliveryId, clientId, jeeberId, deliveredAt, ct);

    public Task<RatingPair> SubmitAsync(
        string deliveryId,
        bool callerIsClient,
        RatingEntry entry,
        CancellationToken ct) =>
        _inner.SubmitAsync(deliveryId, callerIsClient, entry, ct);

    public Task<RatingWindowSweepResult> SweepExpiredWindowsAsync(
        DateTimeOffset deliveredAtCutoff,
        DateTimeOffset processedAt,
        CancellationToken ct)
    {
        _logger.LogCritical(
            "Rating reveal sweep cannot process upstream ratings: ServiceFeedbackClient is missing list-expired-windows and mark-revealed-or-closed operations. Returning zero results rather than fabricating reveal state.");
        return Task.FromResult(new RatingWindowSweepResult(RevealedCount: 0, ClosedCount: 0));
    }

    public Task<IReadOnlyList<JeeberRatingSummary>> ListJeebersBelowAverageAsync(
        double threshold,
        int minRatings,
        CancellationToken ct)
    {
        _logger.LogWarning(
            "Low-rating auto-flag cannot aggregate upstream ratings: ServiceFeedbackClient is missing an all-ratee rating-summary operation.");
        return Task.FromResult<IReadOnlyList<JeeberRatingSummary>>(Array.Empty<JeeberRatingSummary>());
    }
}
