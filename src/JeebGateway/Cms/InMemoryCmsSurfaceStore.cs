using System.Collections.Concurrent;

namespace JeebGateway.Cms;

/// <summary>
/// MVP in-memory implementation of <see cref="ICmsSurfaceStore"/>. Seeded with
/// the four canonical W2 surfaces (<c>ofl-cms-orders/users/wallet/kyc-mfe</c>),
/// each with a published v1 envelope, matching the mock seed in
/// scenario-catalog §5. Registered as a singleton so authoring state survives
/// across requests for the lifetime of the gateway process.
/// </summary>
public sealed class InMemoryCmsSurfaceStore : ICmsSurfaceStore
{
    private readonly ConcurrentDictionary<string, CmsSurface> _bySurfaceId =
        new(StringComparer.Ordinal);

    private readonly object _gate = new();

    public InMemoryCmsSurfaceStore(TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        var seededAt = clock.GetUtcNow();
        Seed("ofl-cms-orders-mfe", "Orders MFE", seededAt);
        Seed("ofl-cms-users-mfe", "Users MFE", seededAt);
        Seed("ofl-cms-wallet-mfe", "Wallet MFE", seededAt);
        Seed("ofl-cms-kyc-mfe", "KYC MFE", seededAt);
    }

    public IReadOnlyList<CmsSurface> ListSurfaces() =>
        _bySurfaceId.Values
            .OrderBy(s => s.SurfaceId, StringComparer.Ordinal)
            .ToList();

    public CmsSurface? GetSurface(string surfaceId)
    {
        _bySurfaceId.TryGetValue(surfaceId, out var surface);
        return surface;
    }

    public CmsSurface? UpsertDraft(string surfaceId, CmsConfig draft)
    {
        lock (_gate)
        {
            if (!_bySurfaceId.TryGetValue(surfaceId, out var surface))
            {
                return null;
            }

            surface.Draft = draft;
            return surface;
        }
    }

    public CmsConfigVersion? Publish(string surfaceId, string publishedByUserId, DateTimeOffset publishedAt)
    {
        lock (_gate)
        {
            if (!_bySurfaceId.TryGetValue(surfaceId, out var surface))
            {
                return null;
            }

            // Snapshot the draft if present, otherwise re-publish the current
            // live config (or empty). PUBLISH never throws on an empty surface.
            var snapshot = surface.Draft
                           ?? surface.LatestPublished?.Config
                           ?? CmsConfig.Empty();

            var version = new CmsConfigVersion
            {
                Version = surface.LatestPublishedVersion + 1,
                Config = snapshot,
                PublishedAt = publishedAt,
                PublishedByUserId = publishedByUserId,
            };

            surface.Versions.Add(version);
            return version;
        }
    }

    private void Seed(string surfaceId, string title, DateTimeOffset at)
    {
        var surface = new CmsSurface { SurfaceId = surfaceId, Title = title };
        surface.Versions.Add(new CmsConfigVersion
        {
            Version = 1,
            Config = new CmsConfig
            {
                Data = new Dictionary<string, object?>
                {
                    ["surfaceId"] = surfaceId,
                    ["enabled"] = true,
                },
            },
            PublishedAt = at,
            PublishedByUserId = "seed",
        });
        _bySurfaceId[surfaceId] = surface;
    }
}
