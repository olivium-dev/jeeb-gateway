using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Stateless anti-corruption adapter over ban-service's generic moderation
/// catalog. Jeeb's product choice is confined to <see cref="JeebModerationList.ListKey"/>;
/// catalog rows, immutable versions, and acknowledgements are owner state.
/// </summary>
public sealed class BanServiceProhibitedItemsStore : IProhibitedItemsStore
{
    public const string ConsumerKey = "jeeb-gateway";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    public BanServiceProhibitedItemsStore(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct) =>
        (await GetActiveCatalogAsync(ct)).Items;

    public async Task<ProhibitedCatalogSnapshot> GetActiveCatalogAsync(CancellationToken ct)
    {
        var snapshot = await GetCurrentSnapshotAsync(ct);
        var items = snapshot.Items
            .Where(item => item.Active)
            .Select(MapItem)
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ProhibitedCatalogSnapshot(items, snapshot.VersionTag!);
    }

    public async Task<ProhibitedItemsPage> ListAllAsync(
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var snapshot = await GetCurrentSnapshotAsync(ct);
        var ordered = snapshot.Items
            .Select(MapItem)
            .OrderByDescending(item => item.UpdatedAt)
            .ToList();

        return new ProhibitedItemsPage
        {
            Items = ordered.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = ordered.Count,
        };
    }

    public async Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) return null;

        var snapshot = await GetCurrentSnapshotAsync(ct);
        var item = snapshot.Items.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase));
        return item is null ? null : MapItem(item);
    }

    public async Task<ProhibitedItem> CreateAsync(
        ProhibitedItemCreate input,
        string adminUserId,
        CancellationToken ct)
    {
        var name = input.Name.Trim();
        var url = $"v1/moderation/admin/prohibited-items?list_key={Escape(JeebModerationList.ListKey)}";
        using var response = await _http.PostAsJsonAsync(url, new WireCreateItem
        {
            ListKey = JeebModerationList.ListKey,
            Category = input.Category,
            Keyword = name,
            Description = input.Description,
            Severity = ToWireSeverity(input.Severity),
            Language = "en",
            Active = true,
            ActorRef = adminUserId,
        }, JsonOptions, ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await MapCatalogConflictAsync(response, name, null, ct);
        }

        response.EnsureSuccessStatusCode();
        return MapItem(await ReadRequiredAsync<WireItem>(response, ct));
    }

    public async Task<ProhibitedItem?> UpdateAsync(
        string id,
        ProhibitedItemPatch patch,
        string adminUserId,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out _)) return null;

        var name = patch.Name?.Trim();
        using var request = new HttpRequestMessage(
            HttpMethod.Put,
            $"v1/moderation/admin/prohibited-items/{Escape(id)}")
        {
            Content = JsonContent.Create(new WireUpdateItem
            {
                ListKey = JeebModerationList.ListKey,
                Category = patch.Category,
                Keyword = name,
                Description = patch.Description,
                Severity = patch.Severity is null ? null : ToWireSeverity(patch.Severity.Value),
                Active = patch.Active,
                ActorRef = adminUserId,
            }, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await MapCatalogConflictAsync(response, name ?? id, null, ct);
        }

        response.EnsureSuccessStatusCode();
        return MapItem(await ReadRequiredAsync<WireItem>(response, ct));
    }

    public async Task<UserAcknowledgment?> GetAcknowledgmentAsync(
        string userId,
        CancellationToken ct)
    {
        var url =
            $"v1/moderation/catalogs/{Escape(JeebModerationList.ListKey)}/acknowledgements/current" +
            $"?consumer_key={Escape(ConsumerKey)}&subject_ref={Escape(userId)}";
        using var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var current = await ReadRequiredAsync<WireCurrentAcknowledgement>(response, ct);

        if (!string.Equals(current.CatalogKey, JeebModerationList.ListKey, StringComparison.Ordinal)
            || !string.Equals(current.ConsumerKey, ConsumerKey, StringComparison.Ordinal)
            || !string.Equals(current.SubjectRef, userId, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(current.CurrentVersionTag))
        {
            throw InvalidOwnerBody(response, "current acknowledgement identity mismatch");
        }

        if (!current.Acknowledged) return null;
        if (current.Acknowledgement is null)
        {
            throw InvalidOwnerBody(response, "acknowledged=true without acknowledgement metadata");
        }

        return MapAcknowledgement(
            current.Acknowledgement,
            userId,
            current.CurrentVersionTag);
    }

    public async Task<UserAcknowledgment?> GetAcknowledgmentAsync(
        string userId,
        string version,
        CancellationToken ct)
    {
        var url =
            "v1/moderation/acknowledgements" +
            $"?catalog_key={Escape(JeebModerationList.ListKey)}" +
            $"&consumer_key={Escape(ConsumerKey)}" +
            $"&subject_ref={Escape(userId)}" +
            $"&version_tag={Escape(version)}";
        using var response = await _http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return MapAcknowledgement(
            await ReadRequiredAsync<WireAcknowledgement>(response, ct),
            userId,
            version);
    }

    public async Task<UserAcknowledgment> AcknowledgeAsync(
        string userId,
        string version,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "v1/moderation/acknowledgements")
        {
            Content = JsonContent.Create(new WireAcknowledgementRequest
            {
                CatalogKey = JeebModerationList.ListKey,
                ConsumerKey = ConsumerKey,
                SubjectRef = userId,
                VersionTag = version,
            }, options: JsonOptions),
        };
        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw await MapCatalogConflictAsync(response, null, version, ct);
        }
        response.EnsureSuccessStatusCode();
        return MapAcknowledgement(
            await ReadRequiredAsync<WireAcknowledgement>(response, ct),
            userId,
            version);
    }

    private async Task<WireCatalogSnapshot> GetCurrentSnapshotAsync(CancellationToken ct)
    {
        var catalogKey = Escape(JeebModerationList.ListKey);
        using var versionsResponse = await _http.GetAsync(
            $"v1/moderation/catalogs/{catalogKey}/versions", ct);
        versionsResponse.EnsureSuccessStatusCode();
        var versions = await ReadRequiredAsync<WireCatalogVersions>(versionsResponse, ct);

        if (!string.Equals(
                versions.CatalogKey,
                JeebModerationList.ListKey,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(versions.CurrentVersionTag))
        {
            throw InvalidOwnerBody(versionsResponse, "missing or mismatched current catalog version");
        }

        using var snapshotResponse = await _http.GetAsync(
            $"v1/moderation/catalogs/{catalogKey}/versions/{Escape(versions.CurrentVersionTag)}",
            ct);
        snapshotResponse.EnsureSuccessStatusCode();
        var snapshot = await ReadRequiredAsync<WireCatalogSnapshot>(snapshotResponse, ct);

        if (!string.Equals(snapshot.CatalogKey, JeebModerationList.ListKey, StringComparison.Ordinal)
            || !string.Equals(snapshot.VersionTag, versions.CurrentVersionTag, StringComparison.Ordinal)
            || snapshot.Items is null
            || snapshot.Items.Any(item => !string.Equals(
                item.ListKey,
                JeebModerationList.ListKey,
                StringComparison.Ordinal)))
        {
            throw InvalidOwnerBody(snapshotResponse, "catalog snapshot identity does not match the pinned version");
        }

        return snapshot;
    }

    private static async Task<T> ReadRequiredAsync<T>(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var value = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
        return value ?? throw InvalidOwnerBody(response, "empty JSON body");
    }

    private static async Task<Exception> MapCatalogConflictAsync(
        HttpResponseMessage response,
        string? duplicateName,
        string? acknowledgedVersion,
        CancellationToken ct)
    {
        WireError? error = null;
        try
        {
            error = await response.Content.ReadFromJsonAsync<WireError>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            // Preserve the owner-conflict semantic if a proxy replaces the
            // structured JSON body with an HTML or otherwise malformed body.
        }
        catch (NotSupportedException)
        {
            // Same posture for an unsupported proxy response content type.
        }

        if (string.Equals(
                error?.Error,
                "CATALOG_KEYWORD_DUPLICATE",
                StringComparison.Ordinal)
            && duplicateName is not null)
        {
            return new DuplicateProhibitedItemNameException(duplicateName);
        }

        if (string.Equals(
                error?.Error,
                "CATALOG_VERSION_STALE",
                StringComparison.Ordinal)
            && acknowledgedVersion is not null)
        {
            return new StaleProhibitedCatalogVersionException(
                acknowledgedVersion,
                error?.Message ?? "The prohibited-items catalog changed before acknowledgement was committed.");
        }

        return new ProhibitedCatalogConflictException(
            error?.Message ?? "ban-service rejected the prohibited-items operation because the owner catalog changed.");
    }

    private static HttpRequestException InvalidOwnerBody(
        HttpResponseMessage response,
        string detail) => new(
            $"ban-service {response.RequestMessage?.RequestUri} returned invalid moderation metadata: {detail}.");

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string ToWireSeverity(ProhibitedSeverity severity) => severity switch
    {
        ProhibitedSeverity.Warn => "warn",
        ProhibitedSeverity.Block => "block",
        _ => throw new ArgumentOutOfRangeException(nameof(severity), severity, null),
    };

    private static ProhibitedSeverity FromWireSeverity(string? severity) => severity switch
    {
        "warn" => ProhibitedSeverity.Warn,
        "block" => ProhibitedSeverity.Block,
        _ => throw new HttpRequestException(
            $"ban-service returned unknown prohibited-item severity '{severity}'."),
    };

    private static ProhibitedItem MapItem(WireItem item)
    {
        if (item.Id is null || !Guid.TryParse(item.Id, out _))
        {
            throw new HttpRequestException("ban-service returned an item without a valid opaque UUID id.");
        }

        if (!string.Equals(item.ListKey, JeebModerationList.ListKey, StringComparison.Ordinal))
        {
            throw new HttpRequestException("ban-service returned an item for a different moderation catalog.");
        }

        if (string.IsNullOrWhiteSpace(item.Keyword)
            || string.IsNullOrWhiteSpace(item.Category))
        {
            throw new HttpRequestException("ban-service returned an item without keyword or category.");
        }

        if (item.CreatedAt == default || item.UpdatedAt == default)
        {
            throw new HttpRequestException("ban-service returned an item without owner timestamps.");
        }

        return new ProhibitedItem
        {
            Id = item.Id,
            Name = item.Keyword,
            Category = item.Category,
            Description = item.Description,
            Severity = FromWireSeverity(item.Severity),
            Active = item.Active,
            CreatedBy = item.CreatedBy,
            UpdatedBy = item.UpdatedBy,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
        };
    }

    private static UserAcknowledgment MapAcknowledgement(
        WireAcknowledgement acknowledgement,
        string expectedUserId,
        string expectedVersion)
    {
        if (!string.Equals(acknowledgement.CatalogKey, JeebModerationList.ListKey, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.ConsumerKey, ConsumerKey, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.SubjectRef, expectedUserId, StringComparison.Ordinal)
            || !string.Equals(acknowledgement.VersionTag, expectedVersion, StringComparison.Ordinal))
        {
            throw new HttpRequestException("ban-service returned a mismatched acknowledgement identity.");
        }

        return new UserAcknowledgment
        {
            UserId = expectedUserId,
            Version = expectedVersion,
            AcknowledgedAt = acknowledgement.AcknowledgedAt,
        };
    }

    private sealed class WireCatalogVersions
    {
        public string? CatalogKey { get; init; }
        public long CurrentRevision { get; init; }
        public string? CurrentVersionTag { get; init; }
    }

    private sealed class WireCatalogSnapshot
    {
        public string? CatalogKey { get; init; }
        public long Revision { get; init; }
        public string? VersionTag { get; init; }
        public List<WireItem> Items { get; init; } = [];
    }

    private sealed class WireItem
    {
        public string? Id { get; init; }
        public string? ListKey { get; init; }
        public string? Category { get; init; }
        public string? Keyword { get; init; }
        public string? Description { get; init; }
        public string? Severity { get; init; }
        public bool Active { get; init; }
        public string? CreatedBy { get; init; }
        public string? UpdatedBy { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
    }

    private sealed class WireCreateItem
    {
        public string? ListKey { get; init; }
        public string? Category { get; init; }
        public string? Keyword { get; init; }
        public string? Description { get; init; }
        public string? Severity { get; init; }
        public string? Language { get; init; }
        public bool Active { get; init; }
        public string? ActorRef { get; init; }
    }

    private sealed class WireUpdateItem
    {
        public string? ListKey { get; init; }
        public string? Category { get; init; }
        public string? Keyword { get; init; }
        public string? Description { get; init; }
        public string? Severity { get; init; }
        public bool? Active { get; init; }
        public string? ActorRef { get; init; }
    }

    private sealed class WireAcknowledgementRequest
    {
        public string? CatalogKey { get; init; }
        public string? ConsumerKey { get; init; }
        public string? SubjectRef { get; init; }
        public string? VersionTag { get; init; }
    }

    private sealed class WireAcknowledgement
    {
        public string? CatalogKey { get; init; }
        public string? ConsumerKey { get; init; }
        public string? SubjectRef { get; init; }
        public string? VersionTag { get; init; }
        public DateTimeOffset AcknowledgedAt { get; init; }
    }

    private sealed class WireCurrentAcknowledgement
    {
        public string? CatalogKey { get; init; }
        public string? ConsumerKey { get; init; }
        public string? SubjectRef { get; init; }
        public string? CurrentVersionTag { get; init; }
        public bool Acknowledged { get; init; }
        public WireAcknowledgement? Acknowledgement { get; init; }
    }

    private sealed class WireError
    {
        public string? Error { get; init; }
        public string? Message { get; init; }
    }
}
