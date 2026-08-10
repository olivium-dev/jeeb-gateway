using JeebGateway.Infrastructure;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Explicit feedback-service capability gate. A complete right-of-access export
/// needs both ratings authored by and ratings received by the user, including
/// their originating request/delivery correlation. The current owner API only
/// lists ratings by ratee and cannot meet that contract; omitting the authored
/// half is forbidden.
/// </summary>
public sealed class FeedbackServiceDataExportRatingsProvider : IDataExportRatingsProvider
{
    public Task<IReadOnlyList<RatingSnapshot>> GetForUserAsync(
        string userId,
        CancellationToken ct) =>
        Task.FromException<IReadOnlyList<RatingSnapshot>>(
            new OwnerCapabilityUnavailableException(
                "feedback-service complete ratings-by-user export (given and received, with request correlation)"));
}
