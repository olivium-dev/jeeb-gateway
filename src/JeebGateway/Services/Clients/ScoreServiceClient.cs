using System.Net.Http.Json;
using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IScoreServiceClient"/>.
/// Targets the shared score-taking-service generic capture primitive
/// POST /api/scores with a product-agnostic body
/// (<c>{ subjectId, authorId, subjectRole?, value, comment?, submittedAt }</c>);
/// the web JSON defaults serialise to camelCase to match the upstream contract.
/// The named HttpClient supplies BaseAddress + the org-standard resilience
/// pipeline (see <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>);
/// this class never has to think about retry/timeout/circuit-breaker.
///
/// AC9 / Golden Rule 4 — tracked debt: replace with an NSwag-generated client
/// once score-taking-service publishes its OpenAPI (see <see cref="IScoreServiceClient"/>).
/// </summary>
public sealed class ScoreServiceClient : IScoreServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public ScoreServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<SubmitScoreUpstreamResponse> SubmitScoreAsync(
        SubmitScoreUpstreamRequest request,
        CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/scores", request, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<SubmitScoreUpstreamResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }
}
