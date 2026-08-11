using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Ratings;
using JeebGateway.Ratings.Jeeb;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Raised when the ratings section of a GDPR export cannot be established.
/// Deliberately fatal to the packaging job: the processor marks the export
/// <c>failed</c> (a terminal state the user can re-request from) instead of
/// delivering a payload that silently claims the user has no ratings.
/// </summary>
public sealed class DataExportRatingsUnavailableException : Exception
{
    public DataExportRatingsUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Gateway-side consumer of feedback-service's internal per-user rating export
/// (<c>GET /internal/ratings/export</c>). feedback-service is the ratings record of
/// truth (<see cref="FeedbackServiceRatingStore"/>), so the packager's previous
/// binding — <see cref="InMemoryDataExportRatingsProvider"/>, seeded only by tests —
/// shipped a silently empty <c>"ratings"</c> section in every production export.
///
/// <para>Jeeb semantics stay here exactly as on the write path: the opaque upstream id
/// is <see cref="FeedbackServiceRatingStore.StableGuid"/> of the Jeeb user id, and the
/// partition + <c>jeeb:delivery:</c> linkage are applied gateway-side so the shared
/// service stays product-agnostic. Upstream withholds still-blind counterparty rows,
/// so the export cannot leak an unrevealed rating.</para>
/// </summary>
public sealed class FeedbackServiceDataExportRatingsProvider : IDataExportRatingsProvider
{
    internal const string HttpClientName = "FeedbackRatingExportClient";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _clients;
    private readonly FeedbackRatingExportOptions _options;
    private readonly ILogger<FeedbackServiceDataExportRatingsProvider> _log;

    public FeedbackServiceDataExportRatingsProvider(
        IHttpClientFactory clients,
        FeedbackRatingExportOptions options,
        ILogger<FeedbackServiceDataExportRatingsProvider> log)
    {
        _clients = clients;
        _options = options;
        _log = log;
    }

    public async Task<IReadOnlyList<RatingSnapshot>> GetForUserAsync(string userId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));

        var opaqueUserId = FeedbackServiceRatingStore.StableGuid(userId);
        var url = $"internal/ratings/export?userId={opaqueUserId:D}&limit={_options.PageLimit}";

        RatingExportPage page;
        try
        {
            using var response = await _clients.CreateClient(HttpClientName).GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new DataExportRatingsUnavailableException(
                    $"feedback-service rating export returned HTTP {(int)response.StatusCode}.");
            }

            page = await response.Content.ReadFromJsonAsync<RatingExportPage>(Json, ct)
                ?? throw new DataExportRatingsUnavailableException(
                    "feedback-service rating export returned an empty body.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
        {
            throw new DataExportRatingsUnavailableException(
                "feedback-service rating export could not be read.", ex);
        }

        if (page.HasMore)
        {
            // Upstream caps a page at 100 and exposes no cursor yet.
            _log.LogWarning(
                "Data-export ratings for user {UserId} are truncated at {Limit} rows: feedback-service " +
                "reported HasMore and offers no export cursor. Pending keyset paging upstream.",
                userId, _options.PageLimit);
        }

        var rows = page.Ratings ?? [];
        var snapshots = new List<RatingSnapshot>(rows.Count);
        var foreign = 0;
        foreach (var item in rows)
        {
            if (!IsJeebRow(item))
            {
                foreign++;
                continue;
            }

            snapshots.Add(new RatingSnapshot
            {
                RatingId = item.Id.ToString("D"),
                RequestId = JeebRatingVocabulary.TryDeliveryForCorrelation(item.CorrelationId, out var deliveryId)
                    ? deliveryId
                    : item.CorrelationId?.Trim() ?? string.Empty,
                Direction = item.Direction ?? string.Empty,
                CounterpartyId = item.CounterpartyId.ToString("D"),
                Stars = item.Score,
                Comment = item.Comment,
                CreatedAt = item.CreatedAt.ToUniversalTime()
            });
        }

        if (foreign > 0)
        {
            _log.LogInformation(
                "Data-export ratings for user {UserId}: {Foreign} non-Jeeb row(s) excluded by the partition filter.",
                userId, foreign);
        }

        return snapshots.OrderBy(r => r.CreatedAt).ToArray();
    }

    // Rows written by every Jeeb submit path carry BOTH markers; either alone is enough
    // to keep a legacy row in, and neither matches another product sharing the service.
    private static bool IsJeebRow(RatingExportItem item) =>
        (item.Tags?.Contains(JeebRatingVocabulary.PartitionValue, StringComparer.Ordinal) ?? false) ||
        JeebRatingVocabulary.TryDeliveryForCorrelation(item.CorrelationId, out _);

    internal sealed class RatingExportPage
    {
        public bool HasMore { get; set; }
        public List<RatingExportItem>? Ratings { get; set; }
    }

    internal sealed class RatingExportItem
    {
        public Guid Id { get; set; }
        public string? CorrelationId { get; set; }
        public string? Direction { get; set; }
        public Guid CounterpartyId { get; set; }
        public int Score { get; set; }
        public string? Comment { get; set; }
        public List<string>? Tags { get; set; }

        // Upstream normalises to UTC and serialises with 'Z'; DateTimeOffset keeps
        // an offset form correct too, which a bare DateTime would misread as local.
        public DateTimeOffset CreatedAt { get; set; }
    }
}
