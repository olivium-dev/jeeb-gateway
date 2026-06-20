namespace JeebGateway.Cms;

/// <summary>Wire DTOs for the CMS authoring plane (WS-01). Kept separate from
/// the domain model so the persisted shape can evolve independently of the
/// HTTP contract.</summary>
public sealed record CmsSurfaceSummaryDto(
    string SurfaceId,
    string Title,
    int PublishedVersion,
    bool HasDraft);

public sealed record CmsSurfaceListResponse(IReadOnlyList<CmsSurfaceSummaryDto> Surfaces);

/// <summary>
/// Published / draft config envelope. <c>config</c> is null for a draft read
/// when no draft has been upserted yet (the controller maps that to 404 to
/// match the mock).
/// </summary>
public sealed record CmsConfigEnvelopeDto(
    string SurfaceId,
    int Version,
    IReadOnlyDictionary<string, object?> Config,
    DateTimeOffset? PublishedAt);

/// <summary>Body for PUT /admin/v1/cms/config/{surfaceId}/draft.</summary>
public sealed record CmsDraftUpsertRequest(IReadOnlyDictionary<string, object?>? Config);

/// <summary>Response for POST /admin/v1/cms/config/{surfaceId}/publish.</summary>
public sealed record CmsPublishResponse(
    string SurfaceId,
    int Version,
    IReadOnlyDictionary<string, object?> Config,
    DateTimeOffset PublishedAt,
    string PublishedByUserId);

/// <summary>One row in GET /admin/v1/cms/config/{surfaceId}/versions.</summary>
public sealed record CmsVersionSummaryDto(
    int Version,
    DateTimeOffset PublishedAt,
    string PublishedByUserId);

public sealed record CmsVersionListResponse(
    string SurfaceId,
    IReadOnlyList<CmsVersionSummaryDto> Versions);

/// <summary>
/// Result of GET /admin/v1/cms/config/{surfaceId}/diff?from=&to=. Reports the
/// keys added, removed, and changed between two published versions.
/// </summary>
public sealed record CmsDiffResponse(
    string SurfaceId,
    int FromVersion,
    int ToVersion,
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<CmsDiffChangedKey> Changed);

public sealed record CmsDiffChangedKey(string Key, object? From, object? To);

/// <summary>GET /admin/v1/cms/dev/step-up-totp helper response.</summary>
public sealed record CmsStepUpDevCodeResponse(string Code, int ExpiresInSeconds);
