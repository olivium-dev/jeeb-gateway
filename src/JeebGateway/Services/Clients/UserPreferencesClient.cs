using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IUserPreferencesClient"/>.
/// Hand-coded against the committed contract
/// <c>contracts/remote-user-preferences.openapi.json</c>.
///
/// The named "remote-user-preferences" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress + the org-standard resilience pipeline, so this class never has
/// to think about retry/timeout/circuit-breaker.
///
/// Wire shapes:
///   GET  /preferences/{user_id}            -> { "<key>": "<value>", ... }   (Preferences map)
///   GET  /preferences/{user_id}/{pref_key} -> { "value": "<value>" }        (PreferenceValue)  | 404
///   POST /preferences/{user_id}/{pref_key} -> 201, body { "value": "<value>" } (PreferenceValue)
/// </summary>
public sealed class UserPreferencesClient : IUserPreferencesClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly HttpClient _http;

    public UserPreferencesClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(
        string userId,
        CancellationToken ct)
    {
        // GET /preferences/{user_id} -> Preferences (additionalProperties: string)
        using var response = await _http.GetAsync($"preferences/{Uri.EscapeDataString(userId)}", ct);
        response.EnsureSuccessStatusCode();

        var map = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>(JsonOptions, ct);
        return map ?? new Dictionary<string, string>();
    }

    public async Task<string?> GetAsync(string userId, string prefKey, CancellationToken ct)
    {
        // GET /preferences/{user_id}/{pref_key} -> PreferenceValue { value } | 404
        using var response = await _http.GetAsync(
            $"preferences/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(prefKey)}", ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();

        var wire = await response.Content.ReadFromJsonAsync<WirePreferenceValue>(JsonOptions, ct);
        return wire?.Value;
    }

    public async Task SetAsync(string userId, string prefKey, string value, CancellationToken ct)
    {
        // POST /preferences/{user_id}/{pref_key} with body { "value": "<value>" }
        using var response = await _http.PostAsJsonAsync(
            $"preferences/{Uri.EscapeDataString(userId)}/{Uri.EscapeDataString(prefKey)}",
            new WirePreferenceValue { Value = value },
            JsonOptions,
            ct);

        response.EnsureSuccessStatusCode();
    }

    // --- wire DTO (snake_case as defined by the upstream PreferenceValue schema) ---

    private sealed class WirePreferenceValue
    {
        [JsonPropertyName("value")] public string Value { get; init; } = string.Empty;
    }
}
