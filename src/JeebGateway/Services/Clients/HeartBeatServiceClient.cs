using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="IHeartBeatServiceClient"/>.
/// Targets the heart-beat presence routes (ADR-HB-001 §4):
/// <c>PATCH /v1/presence</c> and <c>GET /v1/presence/{userId}</c>.
///
/// <para>
/// Hand-coded (the NSwag client-generation precedent for an upstream with no
/// reachable OpenAPI doc — heart-beat is not yet deployed, so the build host
/// cannot fetch <c>/openapi.json</c>; identical to the
/// <see cref="OfferServiceClient"/> / <see cref="BanServiceClient"/> /
/// <see cref="ContractSigningServiceClient"/> precedent). The wire shape is
/// camelCase, which the shared <c>JsonSerializerDefaults.Web</c> options already
/// emit, so no per-DTO naming attributes are needed for correctness — they are on
/// the DTOs only to lock the contract. Regenerate via NSwag once heart-beat ships
/// an OpenAPI doc.
/// </para>
/// </summary>
public sealed class HeartBeatServiceClient : IHeartBeatServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public HeartBeatServiceClient(HttpClient http)
    {
        _http = http;
    }

    /// <inheritdoc />
    public async Task<HeartBeatPresence> SetPresenceAsync(HeartBeatPresenceRequest body, CancellationToken ct)
    {
        // PATCH /v1/presence — the toggle (N13 home). camelCase body.
        using var request = new HttpRequestMessage(HttpMethod.Patch, "v1/presence")
        {
            Content = JsonContent.Create(body, options: JsonOptions)
        };
        using var response = await _http.SendAsync(request, ct);

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<HeartBeatPresence>(response, ct);
        }

        var reason = await TryReadReasonAsync(response, ct);
        throw new HeartBeatPresenceException((int)response.StatusCode, reason);
    }

    /// <inheritdoc />
    public async Task<HeartBeatPresence?> GetPresenceAsync(string userId, CancellationToken ct)
    {
        // GET /v1/presence/{userId} — pure read. 404 (no presence row yet) maps to
        // null so the controller returns a never-online default instead of a 500.
        using var response = await _http.GetAsync(
            $"v1/presence/{Uri.EscapeDataString(userId)}",
            ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (response.IsSuccessStatusCode)
        {
            return await DeserializeAsync<HeartBeatPresence>(response, ct);
        }

        var reason = await TryReadReasonAsync(response, ct);
        throw new HeartBeatPresenceException((int)response.StatusCode, reason);
    }

    private static async Task<T> DeserializeAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException($"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }

    /// <summary>
    /// Reads the <c>reason</c> field off a non-2xx response, tolerating a
    /// missing/non-JSON body (a proxy 5xx page) — returns null in that case.
    /// </summary>
    private static async Task<string?> TryReadReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        try
        {
            var body = await response.Content.ReadFromJsonAsync<ReasonBody>(JsonOptions, ct);
            return body?.Reason;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ReasonBody(
        [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string? Reason);
}
