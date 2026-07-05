using System.Text.RegularExpressions;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Tiers;

/// <summary>
/// Postgres-backed <see cref="ITiersStore"/> (JEBV4-125, AUDIT-A IN-MEM-LIVE
/// durability follow-up).
///
/// Replaces <see cref="InMemoryTiersStore"/> in production. The admin tier
/// catalog (add / rename / re-price / remove) is the gateway's own system of
/// record for delivery tiers and used to live only in process memory —
/// evaporating on every restart / replica move back to the seeded defaults.
/// This store persists it to the <c>tiers</c> table (migration 0029), whose
/// seed rows mirror <see cref="InMemoryTiersStore"/>'s five defaults byte-for-byte.
///
/// <para>Semantics are preserved exactly:
/// <list type="bullet">
/// <item>Reads return fresh rows every call (the DB is the snapshot), so a
/// concurrent edit "takes effect on the next request only" — the same acceptance
/// criterion the in-memory store satisfied via deep-cloned snapshots.</item>
/// <item>Id defaults to a slug of the name when not supplied — identical
/// <see cref="Slugify"/> logic to the in-memory store.</item>
/// <item>Duplicate id → <see cref="DuplicateTierIdException"/>; duplicate name
/// (case-insensitive) → <see cref="DuplicateTierNameException"/>. Enforced by an
/// explicit pre-check (for clean messages) AND the DB constraints
/// (<c>tiers_pkey</c> / <c>uq_tiers_name_lower</c>) as a race backstop.</item>
/// <item><see cref="ReplaceAsync"/> returns <c>null</c> when the id is unknown
/// (→ 404 at the controller), never creating a row.</item>
/// </list></para>
/// </summary>
public sealed class PostgresTiersStore : ITiersStore
{
    private const string NameUniqueConstraint = "uq_tiers_name_lower";

    private static readonly Regex NonSlugChars = new("[^a-z0-9]+", RegexOptions.Compiled);

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresTiersStore> _log;

    public PostgresTiersStore(INpgsqlConnectionFactory db, ILogger<PostgresTiersStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IReadOnlyList<DeliveryTier>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT id, name, sla_hours, radius_km, commission_rate, price_hint,
                   created_by, updated_by, created_at, updated_at
            FROM tiers
            ORDER BY sla_hours ASC, LOWER(name) ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await ReadListAsync(cmd, ct);
    }

    public async Task<DeliveryTier?> GetAsync(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await GetAsync(conn, id, ct);
    }

    public async Task<DeliveryTier> CreateAsync(DeliveryTierCreate input, string adminUserId, CancellationToken ct)
    {
        var id = string.IsNullOrWhiteSpace(input.Id) ? Slugify(input.Name) : input.Id.Trim();
        var name = input.Name.Trim();
        var priceHint = input.PriceHint.Trim();

        await using var conn = await _db.OpenAsync(ct);

        // Explicit pre-checks first (id then name), mirroring InMemoryTiersStore's
        // ordered checks so error messages are precise. The unique constraints below
        // are the authoritative race backstop.
        if (await ExistsByIdAsync(conn, id, ct)) throw new DuplicateTierIdException(id);
        if (await HasNameConflictAsync(conn, name, excludingId: null, ct)) throw new DuplicateTierNameException(name);

        const string sql = """
            INSERT INTO tiers (id, name, sla_hours, radius_km, commission_rate, price_hint,
                               created_by, updated_by, created_at, updated_at)
            VALUES (@Id, @Name, @SlaHours, @RadiusKm, @CommissionRate, @PriceHint,
                    @AdminUserId, @AdminUserId, now(), now())
            RETURNING id, name, sla_hours, radius_km, commission_rate, price_hint,
                      created_by, updated_by, created_at, updated_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Name", name);
        cmd.Parameters.AddWithValue("SlaHours", input.SlaHours);
        cmd.Parameters.AddWithValue("RadiusKm", input.RadiusKm);
        cmd.Parameters.AddWithValue("CommissionRate", input.CommissionRate);
        cmd.Parameters.AddWithValue("PriceHint", priceHint);
        cmd.Parameters.AddWithValue("AdminUserId", adminUserId);

        try
        {
            var rows = await ReadListAsync(cmd, ct);
            _log.LogInformation("Tier created id={TierId} by={AdminUserId}", id, adminUserId);
            return rows[0];
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // Race backstop: a concurrent insert of the same id/name slipped between
            // the pre-check and the INSERT. Map by which constraint fired.
            if (string.Equals(ex.ConstraintName, NameUniqueConstraint, StringComparison.OrdinalIgnoreCase))
                throw new DuplicateTierNameException(name);
            throw new DuplicateTierIdException(id);
        }
    }

    public async Task<DeliveryTier?> ReplaceAsync(string id, DeliveryTierReplace input, string adminUserId, CancellationToken ct)
    {
        var name = input.Name.Trim();
        var priceHint = input.PriceHint.Trim();

        await using var conn = await _db.OpenAsync(ct);

        // Unknown id → null (never create), matching InMemoryTiersStore.
        if (!await ExistsByIdAsync(conn, id, ct)) return null;
        if (await HasNameConflictAsync(conn, name, excludingId: id, ct)) throw new DuplicateTierNameException(name);

        const string sql = """
            UPDATE tiers
               SET name            = @Name,
                   sla_hours       = @SlaHours,
                   radius_km       = @RadiusKm,
                   commission_rate = @CommissionRate,
                   price_hint      = @PriceHint,
                   updated_by      = @AdminUserId,
                   updated_at      = now()
             WHERE id = @Id
            RETURNING id, name, sla_hours, radius_km, commission_rate, price_hint,
                      created_by, updated_by, created_at, updated_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Name", name);
        cmd.Parameters.AddWithValue("SlaHours", input.SlaHours);
        cmd.Parameters.AddWithValue("RadiusKm", input.RadiusKm);
        cmd.Parameters.AddWithValue("CommissionRate", input.CommissionRate);
        cmd.Parameters.AddWithValue("PriceHint", priceHint);
        cmd.Parameters.AddWithValue("AdminUserId", adminUserId);

        try
        {
            var rows = await ReadListAsync(cmd, ct);
            if (rows.Count == 0) return null; // deleted between the existence check and the update
            _log.LogInformation("Tier replaced id={TierId} by={AdminUserId}", id, adminUserId);
            return rows[0];
        }
        catch (PostgresException ex)
            when (ex.SqlState == PostgresErrorCodes.UniqueViolation
                  && string.Equals(ex.ConstraintName, NameUniqueConstraint, StringComparison.OrdinalIgnoreCase))
        {
            throw new DuplicateTierNameException(name);
        }
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "DELETE FROM tiers WHERE id = @Id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (rows > 0) _log.LogInformation("Tier deleted id={TierId}", id);
        return rows > 0;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<DeliveryTier?> GetAsync(NpgsqlConnection conn, string id, CancellationToken ct)
    {
        const string sql = """
            SELECT id, name, sla_hours, radius_km, commission_rate, price_hint,
                   created_by, updated_by, created_at, updated_at
            FROM tiers
            WHERE id = @Id
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<bool> ExistsByIdAsync(NpgsqlConnection conn, string id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT 1 FROM tiers WHERE id = @Id LIMIT 1", conn);
        cmd.Parameters.AddWithValue("Id", id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<bool> HasNameConflictAsync(
        NpgsqlConnection conn, string name, string? excludingId, CancellationToken ct)
    {
        const string sql = """
            SELECT 1 FROM tiers
            WHERE LOWER(name) = LOWER(@Name)
              AND (@ExcludingId IS NULL OR id <> @ExcludingId)
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Name", name);
        cmd.Parameters.AddWithValue("ExcludingId", (object?)excludingId ?? DBNull.Value);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    private static async Task<List<DeliveryTier>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<DeliveryTier>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static DeliveryTier MapRow(NpgsqlDataReader r) => new()
    {
        Id             = r.GetString(r.GetOrdinal("id")),
        Name           = r.GetString(r.GetOrdinal("name")),
        SlaHours       = r.GetInt32(r.GetOrdinal("sla_hours")),
        RadiusKm       = r.GetDouble(r.GetOrdinal("radius_km")),
        CommissionRate = r.GetDouble(r.GetOrdinal("commission_rate")),
        PriceHint      = r.GetString(r.GetOrdinal("price_hint")),
        CreatedBy      = r.IsDBNull(r.GetOrdinal("created_by")) ? null : r.GetString(r.GetOrdinal("created_by")),
        UpdatedBy      = r.IsDBNull(r.GetOrdinal("updated_by")) ? null : r.GetString(r.GetOrdinal("updated_by")),
        CreatedAt      = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
        UpdatedAt      = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at")),
    };

    /// <summary>
    /// Slug derivation identical to <see cref="InMemoryTiersStore"/> — lower-case,
    /// non-alphanumeric runs collapsed to a single hyphen, trimmed; empty result
    /// falls back to a short random token.
    /// </summary>
    internal static string Slugify(string name)
    {
        var lowered = name.Trim().ToLowerInvariant();
        var hyphenated = NonSlugChars.Replace(lowered, "-").Trim('-');
        return string.IsNullOrEmpty(hyphenated) ? Guid.NewGuid().ToString("n")[..8] : hyphenated;
    }
}
