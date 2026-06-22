using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// Postgres-backed <see cref="ISavedLocationStore"/> — the DURABLE per-user
/// saved-location store (ACCT-04 / REQ-02). Replaces
/// <see cref="InMemorySavedLocationStore"/> in production so saved delivery
/// addresses survive a gateway restart/redeploy (the in-memory store lost every
/// address on bounce, the "can't choose address" symptom).
///
/// <para>Backed by the <c>saved_locations</c> table (db/migrations/0016), keyed
/// by the caller's JWT identity (<c>user_id</c> TEXT = the <c>sub</c> claim).
/// Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s plain
/// ADO/Npgsql style — no ORM. Behaviour is identical to the in-memory store: the
/// first saved location is the implicit default; setting a new default clears the
/// previous one (REQ-02, enforced both in SQL and by the
/// <c>saved_locations_one_default_per_user</c> partial unique index); deleting
/// the default promotes the oldest remaining.</para>
///
/// <para>The "exactly one default" flip is done inside a single SERIALIZABLE
/// transaction (clear-then-set) so concurrent writers converge safely, matching
/// the per-user lock the in-memory store used.</para>
/// </summary>
public sealed class PostgresSavedLocationStore : ISavedLocationStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresSavedLocationStore> _log;

    public PostgresSavedLocationStore(INpgsqlConnectionFactory db, ILogger<PostgresSavedLocationStore> log)
    {
        _db = db;
        _log = log;
    }

    // List ordered default-first, then oldest-first — matches InMemory ordering
    // (OrderByDescending IsDefault, ThenBy CreatedAt).
    private const string SelectColumns =
        "id, user_id, label, address, latitude, longitude, is_default, created_at, updated_at";

    public async Task<IReadOnlyList<SavedLocation>> ListAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = $"""
            SELECT {SelectColumns} FROM saved_locations
            WHERE user_id = @UserId
            ORDER BY is_default DESC, created_at ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);

        var items = new List<SavedLocation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            items.Add(MapRow(reader));
        return items;
    }

    public async Task<SavedLocation?> GetAsync(string userId, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return null;
        await using var conn = await _db.OpenAsync(ct);
        const string sql = $"""
            SELECT {SelectColumns} FROM saved_locations
            WHERE user_id = @UserId AND id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Id", gid);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRow(reader) : null;
    }

    public async Task<SavedLocation> CreateAsync(string userId, CreateSavedLocationRequest request, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        // REQ-02: first saved location is the implicit default; or honour the
        // caller's explicit request. Count under the transaction.
        var existingCount = await CountAsync(conn, tx, userId, ct);
        var makeDefault = request.IsDefault || existingCount == 0;

        if (makeDefault)
            await ClearDefaultsAsync(conn, tx, userId, ct);

        const string insertSql = $"""
            INSERT INTO saved_locations
                (user_id, label, address, latitude, longitude, is_default, created_at, updated_at)
            VALUES
                (@UserId, @Label, @Address, @Latitude, @Longitude, @IsDefault, now(), now())
            RETURNING {SelectColumns}
            """;
        await using var cmd = new NpgsqlCommand(insertSql, conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Label", request.Label);
        cmd.Parameters.AddWithValue("Address", (object?)request.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Latitude", request.Latitude);
        cmd.Parameters.AddWithValue("Longitude", request.Longitude);
        cmd.Parameters.AddWithValue("IsDefault", makeDefault);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var created = MapRow(reader);
        await reader.CloseAsync();
        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Saved location created userId={UserId} id={Id} isDefault={IsDefault}",
            userId, created.Id, created.IsDefault);
        return created;
    }

    public async Task<SavedLocation?> UpdateAsync(string userId, string id, UpdateSavedLocationRequest request, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        // Ensure the row exists for this user before mutating.
        var existing = await GetInTxAsync(conn, tx, userId, gid, ct);
        if (existing is null)
        {
            await tx.RollbackAsync(ct);
            return null;
        }

        // REQ-02: promoting to default clears the previous default first.
        if (request.IsDefault is true)
            await ClearDefaultsAsync(conn, tx, userId, ct);

        // COALESCE keeps "only provided fields change" (NULL param = untouched).
        // is_default is handled explicitly (tri-state) below.
        const string updateSql = """
            UPDATE saved_locations SET
                label     = COALESCE(@Label, label),
                address   = CASE WHEN @AddressSet THEN @Address ELSE address END,
                latitude  = COALESCE(@Latitude, latitude),
                longitude = COALESCE(@Longitude, longitude),
                is_default = CASE WHEN @IsDefaultSet THEN @IsDefault ELSE is_default END,
                updated_at = now()
            WHERE user_id = @UserId AND id = @Id
            """;
        await using (var cmd = new NpgsqlCommand(updateSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("UserId", userId);
            cmd.Parameters.AddWithValue("Id", gid);
            cmd.Parameters.AddWithValue("Label", (object?)request.Label ?? DBNull.Value);
            // address is nullable + optional: only overwrite when the caller sent the field.
            cmd.Parameters.AddWithValue("AddressSet", request.Address is not null);
            cmd.Parameters.AddWithValue("Address", (object?)request.Address ?? DBNull.Value);
            cmd.Parameters.AddWithValue("Latitude", (object?)request.Latitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("Longitude", (object?)request.Longitude ?? DBNull.Value);
            cmd.Parameters.AddWithValue("IsDefaultSet", request.IsDefault.HasValue);
            cmd.Parameters.AddWithValue("IsDefault", (object?)request.IsDefault ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var updated = await GetInTxAsync(conn, tx, userId, gid, ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var gid)) return false;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);

        const string deleteSql = """
            DELETE FROM saved_locations
            WHERE user_id = @UserId AND id = @Id
            RETURNING is_default
            """;
        bool wasDefault;
        await using (var cmd = new NpgsqlCommand(deleteSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("UserId", userId);
            cmd.Parameters.AddWithValue("Id", gid);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null)
            {
                await tx.RollbackAsync(ct);
                return false;
            }
            wasDefault = (bool)result;
        }

        // REQ-02: removing the default promotes the oldest remaining so the user
        // always has a "my location" while any saved location exists.
        if (wasDefault)
        {
            const string promoteSql = """
                UPDATE saved_locations SET is_default = TRUE, updated_at = now()
                WHERE id = (
                    SELECT id FROM saved_locations
                    WHERE user_id = @UserId
                    ORDER BY created_at ASC
                    LIMIT 1
                )
                """;
            await using var cmd = new NpgsqlCommand(promoteSql, conn, tx);
            cmd.Parameters.AddWithValue("UserId", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM saved_locations WHERE user_id = @UserId", conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ClearDefaultsAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "UPDATE saved_locations SET is_default = FALSE, updated_at = now() WHERE user_id = @UserId AND is_default = TRUE",
            conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<SavedLocation?> GetInTxAsync(NpgsqlConnection conn, NpgsqlTransaction tx, string userId, Guid id, CancellationToken ct)
    {
        const string sql = $"""
            SELECT {SelectColumns} FROM saved_locations
            WHERE user_id = @UserId AND id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapRow(reader) : null;
    }

    private static SavedLocation MapRow(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(0).ToString("N"),
        UserId = r.GetString(1),
        Label = r.GetString(2),
        Address = r.IsDBNull(3) ? null : r.GetString(3),
        Latitude = r.GetDouble(4),
        Longitude = r.GetDouble(5),
        IsDefault = r.GetBoolean(6),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(7),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(8)
    };
}
