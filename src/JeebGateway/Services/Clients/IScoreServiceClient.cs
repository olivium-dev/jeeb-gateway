namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over score-taking-service. The mutual-blind reveal logic
/// lives in the gateway (see <see cref="JeebGateway.Ratings.BlindRevealPolicy"/>);
/// this client only handles the canonical capture of one party's rating
/// in the downstream service.
///
/// Hand-coded against the published score-taking-service routes pending an
/// NSwag spec. Throws <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IScoreServiceClient
{
    Task<SubmitScoreUpstreamResponse> SubmitScoreAsync(
        SubmitScoreUpstreamRequest request,
        CancellationToken ct);
}

public sealed class SubmitScoreUpstreamRequest
{
    public required string DeliveryId { get; init; }
    public required string AuthorUserId { get; init; }
    public required string RateeUserId { get; init; }

    /// <summary><c>client</c> or <c>jeeber</c>.</summary>
    public required string AuthorRole { get; init; }

    public required int Stars { get; init; }
    public string? Comment { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
}

public sealed class SubmitScoreUpstreamResponse
{
    public required string ScoreId { get; init; }
    public required string DeliveryId { get; init; }
    public required DateTimeOffset SubmittedAt { get; init; }
}
