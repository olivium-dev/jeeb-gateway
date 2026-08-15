namespace JeebGateway.Cms;

/// <summary>
/// Compatibility seam for the CMS authoring plane. The runtime implementation
/// is a stateless bundler-service adapter; the gateway owns no CMS persistence.
/// The step-up and capability gates remain at the gateway route boundary.
/// </summary>
public interface ICmsSurfaceStore
{
    /// <summary>Every known surface, ordered by <see cref="CmsSurface.SurfaceId"/>.</summary>
    Task<IReadOnlyList<CmsSurface>> ListSurfacesAsync(CancellationToken ct);

    /// <summary>Returns the surface, or null when <paramref name="surfaceId"/> is unknown.</summary>
    Task<CmsSurface?> GetSurfaceAsync(string surfaceId, CancellationToken ct);

    /// <summary>
    /// Upserts the draft config for a surface. Returns null when the surface
    /// id is unknown (the caller maps that to 404).
    /// </summary>
    Task<CmsSurface?> UpsertDraftAsync(string surfaceId, CmsConfig draft, CancellationToken ct);

    /// <summary>
    /// Snapshots the current draft as the next published version and bumps the
    /// version counter. Returns the newly-created version, or null when the
    /// surface id is unknown. When no draft exists yet, the current published
    /// config (or an empty config) is snapshotted so PUBLISH is always
    /// idempotent-safe and never throws.
    /// </summary>
    Task<CmsConfigVersion?> PublishAsync(
        string surfaceId,
        string publishedByUserId,
        DateTimeOffset publishedAt,
        CancellationToken ct);
}
