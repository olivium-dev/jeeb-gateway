using System.Text.Json;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Cms;

/// <summary>
/// Postgres-backed <see cref="ICmsSurfaceStore"/> (JEBV4-132, AUDIT-A IN-MEM-LIVE
/// durability follow-up).
///
/// Replaces <see cref="InMemoryCmsSurfaceStore"/> in production. The gateway-owned
/// CMS authoring plane (WS-01, W4/W7a) is the system of record for every MFE
/// config envelope (<c>ofl-cms-orders/users/wallet/kyc-mfe</c>) and used to live
/// only in process memory — an admin's draft edits and published config versions
/// evaporated on every restart / replica move, flapping the surfaces back to the
/// seeded v1 defaults. This store persists them to the <c>cms_surfaces</c> +
/// <c>cms_surface_versions</c> tables (migration 0032), whose seed rows mirror
/// <see cref="InMemoryCmsSurfaceStore"/>'s four canonical surfaces byte-for-byte.
///
/// <para>Semantics are preserved exactly:
/// <list type="bullet">
/// <item><see cref="ListSurfaces"/> returns every surface ordered by
/// <c>surface_id</c> (ASCII slug ids — the Postgres ordering matches the
/// in-memory <c>StringComparer.Ordinal</c> order).</item>
/// <item><see cref="GetSurface"/> hydrates the FULL <see cref="CmsSurface"/> —
/// the mutable draft plus the whole append-only published history ordered
/// oldest → newest — so the controller's versions/diff/published reads behave
/// identically to the in-memory store.</item>
/// <item><see cref="UpsertDraft"/> updates the draft JSONB and returns <c>null</c>
/// when the surface id is unknown (→ 404), never creating a surface.</item>
/// <item><see cref="Publish"/> snapshots the current draft (or the latest
/// published config, or an empty config) as the next monotonically-numbered
/// version and never throws on an empty surface. It does NOT clear the draft —
/// mirroring <see cref="InMemoryCmsSurfaceStore.Publish"/>. Returns <c>null</c>
/// for an unknown surface id.</item>
/// </list></para>
///
/// <para><see cref="ICmsSurfaceStore"/> is a synchronous contract (consumed
/// synchronously by <c>CmsAuthoringController</c>), so this store blocks once on
/// <see cref="INpgsqlConnectionFactory.OpenAsync"/> per call and then uses
/// Npgsql's synchronous command APIs. ASP.NET Core has no
/// <see cref="System.Threading.SynchronizationContext"/>, so the single
/// <c>GetAwaiter().GetResult()</c> on connection-open cannot deadlock.</para>
/// </summary>
public sealed class PostgresCmsSurfaceStore : ICmsSurfaceStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresCmsSurfaceStore> _log;

    public PostgresCmsSurfaceStore(INpgsqlConnectionFactory db, ILogger<PostgresCmsSurfaceStore> log)
    {
        _db = db;
        _log = log;
    }

    public IReadOnlyList<CmsSurface> ListSurfaces()
    {
        using var conn = Open();

        // Read the surface ids in the same order the in-memory store produced
        // (Ordinal — for these lowercase ASCII slug ids the Postgres default
        // ordering is identical), then hydrate each fully on the same connection.
        var ids = new List<string>();
        using (var cmd = new NpgsqlCommand(
                   "SELECT surface_id FROM cms_surfaces ORDER BY surface_id ASC", conn))
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                ids.Add(reader.GetString(0));
            }
        }

        var surfaces = new List<CmsSurface>(ids.Count);
        foreach (var id in ids)
        {
            var surface = LoadSurface(conn, id);
            if (surface is not null)
            {
                surfaces.Add(surface);
            }
        }
        return surfaces;
    }

    public CmsSurface? GetSurface(string surfaceId)
    {
        using var conn = Open();
        return LoadSurface(conn, surfaceId);
    }

    public CmsSurface? UpsertDraft(string surfaceId, CmsConfig draft)
    {
        using var conn = Open();

        const string sql = """
            UPDATE cms_surfaces
               SET draft      = @Draft,
                   updated_at = now()
             WHERE surface_id = @SurfaceId
            """;
        using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("SurfaceId", surfaceId);
            cmd.Parameters.Add(new NpgsqlParameter("Draft", NpgsqlDbType.Jsonb)
            {
                Value = SerializeConfig(draft.Data),
            });

            if (cmd.ExecuteNonQuery() == 0)
            {
                return null; // unknown surface id → 404 (never creates a surface)
            }
        }

        // Reload so the returned surface carries the correct LatestPublishedVersion,
        // exactly as the in-memory store returned the mutated surface.
        return LoadSurface(conn, surfaceId);
    }

    public CmsConfigVersion? Publish(string surfaceId, string publishedByUserId, DateTimeOffset publishedAt)
    {
        using var conn = Open();

        var surface = LoadSurface(conn, surfaceId);
        if (surface is null)
        {
            return null; // unknown surface id → 404
        }

        // Snapshot the draft if present, otherwise re-publish the current live
        // config (or empty). PUBLISH never throws on an empty surface — identical
        // to InMemoryCmsSurfaceStore.Publish.
        var snapshot = surface.Draft
                       ?? surface.LatestPublished?.Config
                       ?? CmsConfig.Empty();

        var nextVersion = surface.LatestPublishedVersion + 1;

        const string sql = """
            INSERT INTO cms_surface_versions (surface_id, version, config, published_at, published_by)
            VALUES (@SurfaceId, @Version, @Config, @PublishedAt, @PublishedBy)
            """;
        using (var cmd = new NpgsqlCommand(sql, conn))
        {
            cmd.Parameters.AddWithValue("SurfaceId", surfaceId);
            cmd.Parameters.AddWithValue("Version", nextVersion);
            cmd.Parameters.Add(new NpgsqlParameter("Config", NpgsqlDbType.Jsonb)
            {
                Value = SerializeConfig(snapshot.Data),
            });
            cmd.Parameters.AddWithValue("PublishedAt", publishedAt);
            cmd.Parameters.AddWithValue("PublishedBy", publishedByUserId);
            cmd.ExecuteNonQuery();
        }

        _log.LogInformation(
            "CMS surface published surfaceId={SurfaceId} version={Version} by={PublishedBy}",
            surfaceId, nextVersion, publishedByUserId);

        return new CmsConfigVersion
        {
            Version = nextVersion,
            Config = snapshot,
            PublishedAt = publishedAt,
            PublishedByUserId = publishedByUserId,
        };
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private NpgsqlConnection Open() => _db.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Hydrates the full <see cref="CmsSurface"/> — title + mutable draft + the
    /// whole published history ordered oldest → newest — or <c>null</c> when the
    /// surface id is unknown.
    /// </summary>
    private static CmsSurface? LoadSurface(NpgsqlConnection conn, string surfaceId)
    {
        CmsSurface surface;

        const string surfaceSql = "SELECT title, draft FROM cms_surfaces WHERE surface_id = @SurfaceId LIMIT 1";
        using (var cmd = new NpgsqlCommand(surfaceSql, conn))
        {
            cmd.Parameters.AddWithValue("SurfaceId", surfaceId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var title = reader.GetString(0);
            CmsConfig? draft = reader.IsDBNull(1)
                ? null
                : new CmsConfig { Data = DeserializeConfig(reader.GetString(1)) };

            surface = new CmsSurface { SurfaceId = surfaceId, Title = title, Draft = draft };
        }

        const string versionsSql = """
            SELECT version, config, published_at, published_by
            FROM cms_surface_versions
            WHERE surface_id = @SurfaceId
            ORDER BY version ASC
            """;
        using (var cmd = new NpgsqlCommand(versionsSql, conn))
        {
            cmd.Parameters.AddWithValue("SurfaceId", surfaceId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                surface.Versions.Add(new CmsConfigVersion
                {
                    Version = reader.GetInt32(0),
                    Config = new CmsConfig { Data = DeserializeConfig(reader.GetString(1)) },
                    PublishedAt = reader.GetFieldValue<DateTimeOffset>(2),
                    PublishedByUserId = reader.GetString(3),
                });
            }
        }

        return surface;
    }

    /// <summary>
    /// Serialises the opaque CMS config object to JSON text for the JSONB column.
    /// Values may be original CLR scalars (from a fresh upsert) or
    /// <see cref="JsonElement"/> (round-tripped from the DB); System.Text.Json
    /// renders both to the same JSON.
    /// </summary>
    private static string SerializeConfig(IReadOnlyDictionary<string, object?> data)
        => JsonSerializer.Serialize(data);

    /// <summary>
    /// Deserialises a JSONB config payload back to the opaque key→value map.
    /// JSON values come back as <see cref="JsonElement"/>; the consuming MFE owns
    /// the schema and the wire re-serialisation is byte-identical.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> DeserializeConfig(string json)
        => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)
           ?? new Dictionary<string, object?>();
}
