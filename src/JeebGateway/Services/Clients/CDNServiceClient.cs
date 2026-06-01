using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Services.Clients;

/// <summary>
/// HttpClient-backed implementation of <see cref="ICDNServiceClient"/>.
/// The named "cdn" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies
/// BaseAddress + the org-standard bearer / X-Service-Auth / resilience chain,
/// so this class never has to think about retry/timeout/circuit-breaker.
///
/// Hand-coded against the endpoints JEB-527 / JEB-519 / JEB-59 require because
/// cdn-service exposes no reachable OpenAPI doc yet (not deployed). Mirrors the
/// camelCase System.Text.Json convention the other thin clients bind against.
/// </summary>
public sealed class CDNServiceClient : ICDNServiceClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public CDNServiceClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<CdnAsset> UploadAsync(CdnUploadRequest request, CancellationToken ct)
    {
        // POST /api/v1/assets — multipart: the binary part plus the
        // classification metadata cdn-service needs for retention + scoping.
        using var form = new MultipartFormDataContent();

        var fileContent = new ByteArrayContent(request.Content);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);
        form.Add(fileContent, "file", request.FileName);

        form.Add(new StringContent(request.Category), "category");
        form.Add(new StringContent(request.OwnerUserId), "ownerUserId");
        form.Add(new StringContent(request.RetentionDays.ToString()), "retentionDays");

        using var response = await _http.PostAsync("api/v1/assets", form, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CdnAsset>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }

    public async Task<CdnSignedUrl> GetSignedUrlAsync(
        string assetId,
        int ttlSeconds,
        CancellationToken ct)
    {
        // GET /api/v1/assets/{assetId}/signed-url?ttlSeconds=...
        var url = $"api/v1/assets/{Uri.EscapeDataString(assetId)}/signed-url?ttlSeconds={ttlSeconds}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CdnSignedUrl>(JsonOptions, ct);
        if (payload is null)
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty body.");
        }
        return payload;
    }

    public async Task<CdnAsset?> GetAssetAsync(string assetId, CancellationToken ct)
    {
        // GET /api/v1/assets/{assetId}. A 404 means the asset expired out of the
        // retention window (or never existed) — surfaced as null rather than an
        // exception so callers can render a "no longer available" path.
        var url = $"api/v1/assets/{Uri.EscapeDataString(assetId)}";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<CdnAsset>(JsonOptions, ct);
    }
}
