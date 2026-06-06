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

    public async Task<CdnUploadTicket> MintUploadUrlAsync(
        CdnUploadUrlRequest request,
        CancellationToken ct)
    {
        // POST /api/ImageUpload/presign-put — the LIVE cdn-service signed-PUT
        // broker (DEC1). Mints a signed PUT target + durable objectRef (no bytes).
        // The client PUTs bytes straight to uploadUrl (H2b); they never re-stream
        // the gateway. Live request: { slot, contentType, ttlSeconds, extension };
        // live response: { uploadUrl, method, objectRef, expiresAt, requiredHeaders }.
        var body = new
        {
            slot = request.Slot,
            contentType = request.ContentType,
            ttlSeconds = request.TtlSeconds > 0 ? request.TtlSeconds : 300,
        };

        using var response = await _http.PostAsJsonAsync("api/ImageUpload/presign-put", body, JsonOptions, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<CdnUploadTicketDto>(JsonOptions, ct);
        if (payload is null || string.IsNullOrWhiteSpace(payload.UploadUrl) || string.IsNullOrWhiteSpace(payload.ObjectRef))
        {
            throw new HttpRequestException(
                $"Upstream {response.RequestMessage?.RequestUri} returned an empty/invalid upload ticket.");
        }

        // Live cdn returns an absolute expiresAt; derive a TTL in seconds, falling
        // back to either the explicit expiresInSeconds field or the requested TTL.
        var expiresInSeconds = payload.ExpiresInSeconds;
        if (expiresInSeconds <= 0 && payload.ExpiresAt is { } at)
        {
            expiresInSeconds = (int)Math.Max(0, Math.Round((at - DateTimeOffset.UtcNow).TotalSeconds));
        }
        if (expiresInSeconds <= 0)
        {
            expiresInSeconds = request.TtlSeconds > 0 ? request.TtlSeconds : 300;
        }

        return new CdnUploadTicket
        {
            UploadUrl = payload.UploadUrl!,
            ObjectRef = payload.ObjectRef!,
            ExpiresInSeconds = expiresInSeconds,
        };
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

    // Wire DTO for the LIVE cdn-service presign-put response
    // ({ uploadUrl, method, objectRef, expiresAt, requiredHeaders }). Also
    // tolerates expiresIn / expiresInSeconds for forward-compat.
    private sealed class CdnUploadTicketDto
    {
        public string? UploadUrl { get; set; }
        public string? ObjectRef { get; set; }
        public DateTimeOffset? ExpiresAt { get; set; }
        public int ExpiresInSeconds { get; set; }
        public int ExpiresIn { set => ExpiresInSeconds = value; }
    }
}
