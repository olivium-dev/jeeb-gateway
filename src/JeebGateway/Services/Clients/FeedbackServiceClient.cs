using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IFeedbackServiceClient"/>.
/// Targets feedback-service's <c>Review</c> controller. The named "feedback"
/// HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress + the org-standard bearer / X-Service-Auth / resilience chain,
/// so this class never has to think about retry/timeout/circuit-breaker.
///
/// feedback-service returns camelCase JSON (Swashbuckle / System.Text.Json web
/// defaults: <c>commenterId</c>, <c>averageRating</c>, <c>totalReviewCount</c>),
/// so the default <see cref="JsonSerializerDefaults.Web"/> options bind it
/// without per-field attributes.
/// </summary>
public sealed class FeedbackServiceClient : IFeedbackServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public FeedbackServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<FeedbackCommentResponse> SubmitCommentAsync(
        FeedbackSubmitRequest request,
        CancellationToken ct)
    {
        // POST /Review/comment — CreateCommentRequest. Required upstream:
        // commenterId (GUID), rating (1..5), tag (1..100), criteria (1..50).
        var body = new CreateCommentWire
        {
            CommenterId = request.CommenterId,
            Rating = request.Rating,
            Tag = request.Tag,
            Criteria = request.Criteria,
            Text = request.Text,
            ReviewTitle = request.ReviewTitle,
        };

        using var response = await _http.PostAsJsonAsync("Review/comment", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<FeedbackCommentResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }

    public async Task<double?> GetAverageRatingAsync(string tag, CancellationToken ct)
    {
        // GET /Review/rating?tag=...
        var url = $"Review/rating?tag={Uri.EscapeDataString(tag)}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<AverageRatingWire>(JsonOptions, ct);
        return payload?.AverageRating;
    }

    public async Task<FeedbackCommentsPage> ListCommentsAsync(
        string tag,
        int length,
        int offset,
        CancellationToken ct)
    {
        // GET /Review/comment?Tag=...&Length=...&Offset=...
        var url = $"Review/comment?Tag={Uri.EscapeDataString(tag)}&Length={length}&Offset={offset}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<GetCommentsWire>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }

        return new FeedbackCommentsPage
        {
            Comments = payload.Comments ?? new List<FeedbackCommentResponse>(),
            TotalReviewCount = payload.TotalReviewCount,
            AverageRating = payload.AverageRating,
        };
    }

    // --- wire DTOs ---

    private sealed class CreateCommentWire
    {
        public required string CommenterId { get; init; }
        public required int Rating { get; init; }
        public required string Tag { get; init; }
        public required string Criteria { get; init; }
        public string? Text { get; init; }
        public string? ReviewTitle { get; init; }
    }

    private sealed class AverageRatingWire
    {
        public double? AverageRating { get; init; }
    }

    private sealed class GetCommentsWire
    {
        public List<FeedbackCommentResponse>? Comments { get; init; }
        public int TotalReviewCount { get; init; }
        public double AverageRating { get; init; }
    }
}
