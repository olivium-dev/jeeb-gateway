using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.Artifacts;

/// <summary>
/// Approved private object-storage seam for generated exports. State-service
/// stores only the returned opaque reference and expiry; the gateway never
/// persists artifact bytes locally.
/// </summary>
public interface IPrivateArtifactStore
{
    /// <summary>
    /// Recovers the completed response for an earlier upload command. Durable
    /// workers must call this before reconstructing mutable source data after
    /// an ambiguous upload result. Returns null only when the owner has no
    /// completed upload for the exact idempotency key.
    /// </summary>
    Task<PrivateArtifact?> RecoverUploadAsync(
        string idempotencyKey,
        CancellationToken ct);

    Task<PrivateArtifact> PutAsync(
        string idempotencyKey,
        string ownerRef,
        string fileName,
        string contentType,
        byte[] content,
        DateTimeOffset expiresAt,
        CancellationToken ct);

    Task<PrivateArtifactDownload> CreateDownloadUrlAsync(
        string artifactRef,
        TimeSpan validity,
        bool singleUse,
        CancellationToken ct);

    Task DeleteAsync(string artifactRef, CancellationToken ct);
}

public sealed record PrivateArtifact(
    string ArtifactRef,
    DateTimeOffset ExpiresAt,
    long SizeBytes);

public sealed record PrivateArtifactDownload(
    Uri Url,
    DateTimeOffset ExpiresAt);

public sealed class PrivateArtifactStoreOptions
{
    public const string BaseUrlKey = "PRIVATE_ARTIFACT_STORE_BASE_URL";
    public const string BearerTokenFileKey = "PRIVATE_ARTIFACT_STORE_BEARER_TOKEN_FILE";

    public int MaxArtifactBytes { get; init; } = 25 * 1024 * 1024;
}

/// <summary>
/// Minimal generic HTTP contract expected from the private artifact owner:
/// service-authenticated put/delete and a short-lived private, optionally
/// single-use GET URL. Configuration is validated on every operation so a
/// missing owner or secret fails closed without a local/in-memory fallback.
/// </summary>
public sealed class PrivateArtifactStoreHttpClient(
    HttpClient http,
    IConfiguration configuration,
    PrivateArtifactStoreOptions options) : IPrivateArtifactStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<PrivateArtifact?> RecoverUploadAsync(
        string idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 256)
            throw new ArgumentException(
                "Private artifact idempotency key must contain 1 to 256 characters.",
                nameof(idempotencyKey));

        using var request = await CreateRequestAsync(
            HttpMethod.Get,
            "v1/private-artifacts/by-idempotency-key",
            ct);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        using var response = await http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, "recover-upload", ct);
        var payload = await response.Content.ReadFromJsonAsync<PrivateArtifactWire>(Json, ct)
                      ?? throw new HttpRequestException(
                          "Private artifact recovery returned an empty body.");
        if (string.IsNullOrWhiteSpace(payload.ArtifactRef)
            || payload.ExpiresAt == default
            || payload.SizeBytes < 1)
            throw new HttpRequestException(
                "Private artifact recovery returned invalid metadata.");
        return new PrivateArtifact(payload.ArtifactRef, payload.ExpiresAt, payload.SizeBytes);
    }

    public async Task<PrivateArtifact> PutAsync(
        string idempotencyKey,
        string ownerRef,
        string fileName,
        string contentType,
        byte[] content,
        DateTimeOffset expiresAt,
        CancellationToken ct)
    {
        if (content.Length is 0 || content.Length > options.MaxArtifactBytes)
            throw new InvalidOperationException(
                $"Private artifact content must contain 1 to {options.MaxArtifactBytes} bytes.");

        using var form = new MultipartFormDataContent();
        var bytes = new ByteArrayContent(content);
        bytes.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(bytes, "file", fileName);
        form.Add(new StringContent(ownerRef), "ownerRef");
        form.Add(new StringContent(expiresAt.ToUniversalTime().ToString("O")), "expiresAt");

        using var request = await CreateRequestAsync(HttpMethod.Post, "v1/private-artifacts", ct);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = form;
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "put", ct);
        var payload = await response.Content.ReadFromJsonAsync<PrivateArtifactWire>(Json, ct)
                      ?? throw new HttpRequestException("Private artifact put returned an empty body.");
        if (string.IsNullOrWhiteSpace(payload.ArtifactRef)
            || payload.ExpiresAt <= DateTimeOffset.UtcNow
            || payload.SizeBytes < 1)
            throw new HttpRequestException("Private artifact put returned invalid metadata.");
        return new PrivateArtifact(payload.ArtifactRef, payload.ExpiresAt, payload.SizeBytes);
    }

    public async Task<PrivateArtifactDownload> CreateDownloadUrlAsync(
        string artifactRef,
        TimeSpan validity,
        bool singleUse,
        CancellationToken ct)
    {
        var seconds = Math.Clamp((int)Math.Ceiling(validity.TotalSeconds), 1, 300);
        var path = $"v1/private-artifacts/{Uri.EscapeDataString(artifactRef)}/download-url";
        using var request = await CreateRequestAsync(HttpMethod.Post, path, ct);
        request.Content = JsonContent.Create(new { expiresInSeconds = seconds, singleUse }, options: Json);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccessAsync(response, "download-url", ct);
        var payload = await response.Content.ReadFromJsonAsync<PrivateDownloadWire>(Json, ct)
                      ?? throw new HttpRequestException("Private artifact download-url returned an empty body.");
        if (!Uri.TryCreate(payload.Url, UriKind.Absolute, out var url)
            || !string.Equals(url.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || payload.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new HttpRequestException("Private artifact download-url returned invalid metadata.");
        return new PrivateArtifactDownload(url, payload.ExpiresAt);
    }

    public async Task DeleteAsync(string artifactRef, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(
            HttpMethod.Delete,
            $"v1/private-artifacts/{Uri.EscapeDataString(artifactRef)}",
            ct);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return;
        await EnsureSuccessAsync(response, "delete", ct);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativePath,
        CancellationToken ct)
    {
        var baseUrl = configuration[PrivateArtifactStoreOptions.BaseUrlKey];
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
            || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException(
                $"{PrivateArtifactStoreOptions.BaseUrlKey} must be an absolute HTTP(S) URL.");

        var tokenFile = configuration[PrivateArtifactStoreOptions.BearerTokenFileKey];
        if (string.IsNullOrWhiteSpace(tokenFile) || !Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException(
                $"{PrivateArtifactStoreOptions.BearerTokenFileKey} must be an absolute mounted-secret path.");

        string token;
        try
        {
            token = (await File.ReadAllTextAsync(tokenFile, ct)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("Private artifact service token is unavailable.", ex);
        }
        if (token.Length < 32 || token.Length > 4096 || token.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("Private artifact service token is invalid.");

        var requestUri = new Uri(
            baseUri.AbsoluteUri.EndsWith('/') ? baseUri : new Uri(baseUri.AbsoluteUri + "/"),
            relativePath);
        var request = new HttpRequestMessage(method, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;
        var detail = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"Private artifact {operation} failed with HTTP {(int)response.StatusCode}: {detail}",
            null,
            response.StatusCode);
    }

    private sealed class PrivateArtifactWire
    {
        public string ArtifactRef { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
        public long SizeBytes { get; init; }
    }

    private sealed class PrivateDownloadWire
    {
        public string Url { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAt { get; init; }
    }
}
