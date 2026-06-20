namespace JeebGateway.Cms;

/// <summary>
/// WS-01 (W4/W7a CMS authoring plane). Domain model for a single CMS
/// "surface" — the unit that drives one micro-frontend's config envelope
/// (e.g. <c>ofl-cms-orders-mfe</c>). A surface owns:
/// <list type="bullet">
///   <item>a mutable <b>draft</b> config (upserted via PUT),</item>
///   <item>an immutable, monotonically-versioned <b>published</b> history
///     (each PUBLISH snapshots the current draft as a new version).</item>
/// </list>
/// This is a gateway-owned surface; the mock mounts it under
/// <c>/gateway/admin/v1/cms/*</c>. Step-up TOTP gates PUBLISH (see
/// <see cref="CmsStepUpValidator"/>). All state is in-memory for the MVP —
/// the row shapes mirror what a Postgres-backed store would eventually own.
/// </summary>
public sealed class CmsSurface
{
    public required string SurfaceId { get; init; }

    public required string Title { get; init; }

    /// <summary>Mutable working copy. Null until the first draft upsert.</summary>
    public CmsConfig? Draft { get; set; }

    /// <summary>
    /// Append-only published history, ordered oldest → newest. The last
    /// element is the live published envelope. Empty until the first PUBLISH.
    /// </summary>
    public List<CmsConfigVersion> Versions { get; } = new();

    public int LatestPublishedVersion =>
        Versions.Count == 0 ? 0 : Versions[^1].Version;

    public CmsConfigVersion? LatestPublished =>
        Versions.Count == 0 ? null : Versions[^1];
}

/// <summary>
/// The opaque config payload a surface carries. The gateway treats it as a
/// JSON object (string keys → arbitrary JSON values); the consuming MFE owns
/// the schema. Stored as a normalised dictionary so version diffing is
/// deterministic regardless of upstream key ordering.
/// </summary>
public sealed class CmsConfig
{
    public required IReadOnlyDictionary<string, object?> Data { get; init; }

    public static CmsConfig Empty() =>
        new() { Data = new Dictionary<string, object?>() };
}

/// <summary>One immutable entry in a surface's published history.</summary>
public sealed class CmsConfigVersion
{
    public required int Version { get; init; }

    public required CmsConfig Config { get; init; }

    public required DateTimeOffset PublishedAt { get; init; }

    public required string PublishedByUserId { get; init; }
}
