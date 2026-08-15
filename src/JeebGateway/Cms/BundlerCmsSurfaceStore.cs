using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace JeebGateway.Cms;

/// <summary>
/// Stateless CMS compatibility adapter over bundler-service's namespaced,
/// immutable document API. The gateway retains the established CMS routes and
/// DTOs but owns no surface, draft, or publication row.
/// </summary>
public sealed class BundlerCmsSurfaceStore : ICmsSurfaceStore
{
    public const string HttpClientName = "BundlerCmsSurfaceStore";
    public const string BaseUrlConfigurationKey = "BUNDLER_CMS_BASE_URL";
    public const string NamespaceConfigurationKey = "BUNDLER_CMS_NAMESPACE";
    public const string BearerTokenFileConfigurationKey = "BUNDLER_CMS_BEARER_TOKEN_FILE";
    private const int PageSize = 200;
    private const int MaximumTokenFileBytes = 4096;

    private static readonly Regex NamespacePattern = new(
        "^[a-z0-9][a-z0-9._-]{0,99}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _namespace;
    private readonly string _bearerTokenFile;

    public BundlerCmsSurfaceStore(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _httpClientFactory = httpClientFactory;
        _namespace = RequiredConfiguration(configuration, NamespaceConfigurationKey);
        if (!NamespacePattern.IsMatch(_namespace))
        {
            throw new InvalidOperationException(
                $"{NamespaceConfigurationKey} is not a valid Bundler namespace.");
        }

        _bearerTokenFile = RequiredConfiguration(
            configuration, BearerTokenFileConfigurationKey);
        if (!Path.IsPathRooted(_bearerTokenFile))
        {
            throw new InvalidOperationException(
                $"{BearerTokenFileConfigurationKey} must be an absolute secret-file path.");
        }
    }

    public async Task<IReadOnlyList<CmsSurface>> ListSurfacesAsync(CancellationToken ct)
    {
        var documents = new List<BundlerDocument>();
        string? after = null;
        while (true)
        {
            var cursor = after is null
                ? string.Empty
                : $"&after={Uri.EscapeDataString(after)}";
            using var response = await SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v1/namespaces/{_namespace}/documents?limit={PageSize}&archived=false{cursor}"),
                ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<BundlerDocumentPage>(Json, ct)
                       ?? throw new HttpRequestException(
                           "bundler document list response is empty");
            if (page.Count != page.Documents.Count)
            {
                throw new HttpRequestException(
                    "bundler document list count does not match its rows");
            }
            documents.AddRange(page.Documents);
            if (!page.HasMore)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(page.NextAfter)
                || string.Equals(page.NextAfter, after, StringComparison.Ordinal))
            {
                throw new HttpRequestException(
                    "bundler document list returned a non-advancing cursor");
            }
            after = page.NextAfter;
        }

        var surfaces = new List<CmsSurface>();
        foreach (var document in documents)
        {
            if (document.ArchivedAt is not null)
            {
                continue;
            }
            // The generic Bundler collection intentionally exposes only
            // document heads. Jeeb's title is versioned inside our content
            // envelope, so hydrate the current head instead of inventing a
            // title from the document key.
            var view = await GetViewAsync(document.Key, ct)
                       ?? throw new HttpRequestException(
                           $"bundler listed document '{document.Key}' but its head is missing");
            surfaces.Add(SummarySurface(document, CurrentEnvelope(view).Title));
        }

        return surfaces
            .OrderBy(surface => surface.SurfaceId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<CmsSurface?> GetSurfaceAsync(string surfaceId, CancellationToken ct)
    {
        var view = await GetViewAsync(surfaceId, ct);
        if (view is null || view.Document.ArchivedAt is not null)
        {
            return null;
        }

        var versions = await ListVersionsAsync(surfaceId, ct);
        var publications = await ListPublicationsAsync(surfaceId, ct);
        var contentByVersion = versions.ToDictionary(version => version.Version);
        var current = CurrentEnvelope(view);
        var surface = new CmsSurface
        {
            SurfaceId = surfaceId,
            Title = current.Title,
            Draft = view.Document.CurrentDraftVersion > view.Document.CurrentPublishedVersion
                && view.Draft is not null
                    ? DecodeEnvelope(view.Draft.Content).Config
                    : null,
        };

        foreach (var publication in publications.OrderBy(row => row.Publication))
        {
            if (!contentByVersion.TryGetValue(publication.Version, out var version))
            {
                throw new HttpRequestException(
                    $"bundler publication {publication.Publication} references a missing version");
            }
            surface.Versions.Add(new CmsConfigVersion
            {
                Version = checked((int)publication.Publication),
                Config = DecodeEnvelope(version.Content).Config,
                PublishedAt = publication.PublishedAt,
                PublishedByUserId = publication.PublishedBy,
            });
        }

        return surface;
    }

    public async Task<CmsSurface?> UpsertDraftAsync(
        string surfaceId,
        CmsConfig draft,
        CancellationToken ct)
    {
        var current = await GetViewAsync(surfaceId, ct);
        if (current is null || current.Document.ArchivedAt is not null)
        {
            return null;
        }
        var title = CurrentEnvelope(current).Title;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/namespaces/{_namespace}/documents/{Uri.EscapeDataString(surfaceId)}/drafts")
        {
            Content = JsonContent.Create(new
            {
                expectedDraftVersion = current.Document.CurrentDraftVersion,
                content = new { title, config = draft.Data },
            }, options: Json),
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            OperationKey("draft", surfaceId, current.Document.CurrentDraftVersion));

        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await GetSurfaceAsync(surfaceId, ct);
    }

    public async Task<CmsConfigVersion?> PublishAsync(
        string surfaceId,
        string publishedByUserId,
        DateTimeOffset publishedAt,
        CancellationToken ct)
    {
        var current = await GetViewAsync(surfaceId, ct);
        if (current is null || current.Document.ArchivedAt is not null)
        {
            return null;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"api/v1/namespaces/{_namespace}/documents/{Uri.EscapeDataString(surfaceId)}/publish")
        {
            Content = JsonContent.Create(new
            {
                draftVersion = current.Document.CurrentDraftVersion,
                expectedPublication = current.Document.CurrentPublication,
            }, options: Json),
        };
        request.Headers.TryAddWithoutValidation(
            "Idempotency-Key",
            OperationKey(
                "publish",
                surfaceId,
                current.Document.CurrentDraftVersion,
                current.Document.CurrentPublication));
        using var response = await SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var published = await response.Content.ReadFromJsonAsync<BundlerDocumentView>(Json, ct);
        if (published?.Publication is null || published.Published is null)
        {
            throw new HttpRequestException("bundler publish response is invalid");
        }

        return new CmsConfigVersion
        {
            Version = checked((int)published.Publication.Publication),
            Config = DecodeEnvelope(published.Published.Content).Config,
            PublishedAt = published.Publication.PublishedAt,
            PublishedByUserId = published.Publication.PublishedBy,
        };
    }

    private async Task<BundlerDocumentView?> GetViewAsync(
        string surfaceId,
        CancellationToken ct)
    {
        using var response = await SendAsync(
            new HttpRequestMessage(
                HttpMethod.Get,
                $"api/v1/namespaces/{_namespace}/documents/{Uri.EscapeDataString(surfaceId)}"),
            ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BundlerDocumentView>(Json, ct)
               ?? throw new HttpRequestException("bundler document response is empty");
    }

    private async Task<IReadOnlyList<BundlerVersion>> ListVersionsAsync(
        string surfaceId,
        CancellationToken ct)
    {
        var result = new List<BundlerVersion>();
        long after = 0;
        while (true)
        {
            using var response = await SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v1/namespaces/{_namespace}/documents/{Uri.EscapeDataString(surfaceId)}/versions?after={after}&limit={PageSize}"),
                ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<BundlerVersionPage>(Json, ct)
                       ?? throw new HttpRequestException("bundler versions response is empty");
            result.AddRange(page.Versions);
            if (page.Versions.Count < PageSize)
            {
                return result;
            }
            after = page.Versions[^1].Version;
        }
    }

    private async Task<IReadOnlyList<BundlerPublication>> ListPublicationsAsync(
        string surfaceId,
        CancellationToken ct)
    {
        var result = new List<BundlerPublication>();
        long after = 0;
        while (true)
        {
            using var response = await SendAsync(
                new HttpRequestMessage(
                    HttpMethod.Get,
                    $"api/v1/namespaces/{_namespace}/documents/{Uri.EscapeDataString(surfaceId)}/publications?after={after}&limit={PageSize}"),
                ct);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<BundlerPublicationPage>(Json, ct)
                       ?? throw new HttpRequestException("bundler publications response is empty");
            result.AddRange(page.Publications);
            if (page.Publications.Count < PageSize)
            {
                return result;
            }
            after = page.Publications[^1].Publication;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct)
    {
        var apiKey = await ReadBearerTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return await _httpClientFactory
            .CreateClient(HttpClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<string> ReadBearerTokenAsync(CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(_bearerTokenFile);
            if (!info.Exists || info.Length is < 1 or > MaximumTokenFileBytes)
            {
                throw new InvalidOperationException(
                    $"{BearerTokenFileConfigurationKey} does not reference a bounded secret file.");
            }

            await using var stream = new FileStream(
                info.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024,
                useAsync: true);
            using var reader = new StreamReader(
                stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var token = (await reader.ReadToEndAsync(ct)).Trim();
            if (token.Length < 32 || token.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"{BearerTokenFileConfigurationKey} contains an invalid Bundler credential.");
            }
            return token;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"{BearerTokenFileConfigurationKey} could not be read.", ex);
        }
    }

    private static string RequiredConfiguration(
        IConfiguration configuration, string key)
    {
        var value = configuration[key]?.Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is required.")
            : value;
    }

    private static CmsSurface SummarySurface(BundlerDocument document, string title)
    {
        var surface = new CmsSurface
        {
            SurfaceId = document.Key,
            Title = title,
            Draft = document.CurrentDraftVersion > document.CurrentPublishedVersion
                ? CmsConfig.Empty()
                : null,
        };
        if (document.CurrentPublication > 0)
        {
            surface.Versions.Add(new CmsConfigVersion
            {
                Version = checked((int)document.CurrentPublication),
                Config = CmsConfig.Empty(),
                PublishedAt = document.UpdatedAt,
                PublishedByUserId = "bundler-service",
            });
        }
        return surface;
    }

    private static CmsContentEnvelope CurrentEnvelope(BundlerDocumentView view)
    {
        var head = view.Draft ?? view.Published
            ?? throw new HttpRequestException(
                "bundler CMS document has no versioned content head");
        return DecodeEnvelope(head.Content);
    }

    private static CmsContentEnvelope DecodeEnvelope(JsonElement content)
    {
        if (content.ValueKind != JsonValueKind.Object)
        {
            throw new HttpRequestException(
                "bundler CMS content must be a {title, config} JSON object");
        }
        if (!content.TryGetProperty("title", out var titleElement)
            || titleElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(titleElement.GetString())
            || !content.TryGetProperty("config", out var configElement)
            || configElement.ValueKind != JsonValueKind.Object)
        {
            throw new HttpRequestException(
                "bundler CMS content must contain a non-empty title and object config");
        }
        return new CmsContentEnvelope(
            titleElement.GetString()!,
            new CmsConfig
            {
                Data = configElement.Deserialize<Dictionary<string, object?>>(Json)
                       ?? new Dictionary<string, object?>(),
            });
    }

    private static string OperationKey(string operation, string surfaceId, params long[] heads)
    {
        var source = $"{operation}\u001f{surfaceId}\u001f{string.Join(':', heads)}";
        return "cms-" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private sealed record BundlerDocument(
        [property: JsonPropertyName("documentId")] string DocumentId,
        [property: JsonPropertyName("namespace")] string Namespace,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("currentDraftVersion")] long CurrentDraftVersion,
        [property: JsonPropertyName("currentPublication")] long CurrentPublication,
        [property: JsonPropertyName("currentPublishedVersion")] long CurrentPublishedVersion,
        [property: JsonPropertyName("archivedAt")] DateTimeOffset? ArchivedAt,
        [property: JsonPropertyName("updatedAt")] DateTimeOffset UpdatedAt);

    private sealed record BundlerDocumentPage(
        [property: JsonPropertyName("documents")] IReadOnlyList<BundlerDocument> Documents,
        [property: JsonPropertyName("count")] int Count,
        [property: JsonPropertyName("hasMore")] bool HasMore,
        [property: JsonPropertyName("nextAfter")] string? NextAfter);

    private sealed record BundlerVersion(
        [property: JsonPropertyName("version")] long Version,
        [property: JsonPropertyName("content")] JsonElement Content);

    private sealed record BundlerPublication(
        [property: JsonPropertyName("publication")] long Publication,
        [property: JsonPropertyName("version")] long Version,
        [property: JsonPropertyName("publishedBy")] string PublishedBy,
        [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt);

    private sealed record BundlerDocumentView(
        [property: JsonPropertyName("document")] BundlerDocument Document,
        [property: JsonPropertyName("draft")] BundlerVersion? Draft,
        [property: JsonPropertyName("published")] BundlerVersion? Published,
        [property: JsonPropertyName("publication")] BundlerPublication? Publication);

    private sealed record BundlerVersionPage(
        [property: JsonPropertyName("versions")] IReadOnlyList<BundlerVersion> Versions);

    private sealed record BundlerPublicationPage(
        [property: JsonPropertyName("publications")] IReadOnlyList<BundlerPublication> Publications);

    private sealed record CmsContentEnvelope(string Title, CmsConfig Config);
}
