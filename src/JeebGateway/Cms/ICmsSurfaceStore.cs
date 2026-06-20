namespace JeebGateway.Cms;

/// <summary>
/// Persistence seam for the CMS authoring plane (WS-01). The MVP backs this
/// with an in-memory <see cref="InMemoryCmsSurfaceStore"/>; production swaps
/// in a Postgres-backed implementation colocated with the admin tables. The
/// store is intentionally thin — the step-up gate and capability gate live in
/// the controller / <see cref="CmsStepUpValidator"/>, not here.
/// </summary>
public interface ICmsSurfaceStore
{
    /// <summary>Every known surface, ordered by <see cref="CmsSurface.SurfaceId"/>.</summary>
    IReadOnlyList<CmsSurface> ListSurfaces();

    /// <summary>Returns the surface, or null when <paramref name="surfaceId"/> is unknown.</summary>
    CmsSurface? GetSurface(string surfaceId);

    /// <summary>
    /// Upserts the draft config for a surface. Returns null when the surface
    /// id is unknown (the caller maps that to 404).
    /// </summary>
    CmsSurface? UpsertDraft(string surfaceId, CmsConfig draft);

    /// <summary>
    /// Snapshots the current draft as the next published version and bumps the
    /// version counter. Returns the newly-created version, or null when the
    /// surface id is unknown. When no draft exists yet, the current published
    /// config (or an empty config) is snapshotted so PUBLISH is always
    /// idempotent-safe and never throws.
    /// </summary>
    CmsConfigVersion? Publish(string surfaceId, string publishedByUserId, DateTimeOffset publishedAt);
}
