using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.Matching;

namespace JeebGateway.Services.Clients;

public sealed class MatchingServiceClient : IMatchingServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public MatchingServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<MatchingRunResponse> RunMatchingAsync(MatchingRunRequest body, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync("api/v1/jeeb/matching/run", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MatchingRunResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException("matching-service returned an empty body.");
        }
        return payload;
    }

    /// <inheritdoc />
    public async Task<MatchingServiceMatchesResponse> GetMatchesAsync(
        string userId, int skip, int limit, CancellationToken ct)
    {
        // Real matching-service endpoint: GET /api/v1/matches/{user_id}?skip=&limit=
        // Defined in matching/app/api/endpoints/matches.py router prefix="/matches"
        // mounted at /api/v1 in main.py.
        var relPath = $"api/v1/matches/{Uri.EscapeDataString(userId)}?skip={skip}&limit={limit}";
        using var response = await _http.GetAsync(relPath, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<MatchingServiceMatchesResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException("matching-service returned an empty body for GetMatches.");
        }
        return payload;
    }

    /// <inheritdoc />
    public async Task<MatchingFindJeebersUpstreamResponse> FindJeebersAsync(
        MatchingFindJeebersUpstreamRequest body, CancellationToken ct)
    {
        // Real matching-service route: POST /api/v1/matching/find-jeebers
        // (matching/app/api/endpoints/tier_matching.py, prefix=/matching,
        // mounted at /api/v1 in main.py). Tier-aware: returns online jeebers
        // within tier.radius_km and (when broadcast=true) publishes the match
        // event to the jeeb.requests.<tier> topic.
        using var response = await _http.PostAsJsonAsync(
            "api/v1/matching/find-jeebers", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content
            .ReadFromJsonAsync<MatchingFindJeebersUpstreamResponse>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                "matching-service returned an empty body for FindJeebers.");
        }
        return payload;
    }
}
