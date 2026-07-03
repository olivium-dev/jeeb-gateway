using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Users.SavedLocations;

/// <summary>
/// Postgres-backed <see cref="ISavedLocationStore"/> (ACCT-04 / REQ-02 durability
/// follow-up). Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s
/// raw-Npgsql shape (connection-per-call via <see cref="INpgsqlConnectionFactory"/>,
/// <see cref="NpgsqlCommand"/> + named parameters, static row-mapping helpers).
/// Replaces <see cref="InMemorySavedLocationStore"/> in production, whose rows
/// evaporated on every gateway restart / replica move (ADR-0001: the gateway is
/// stateless and must hold no per-user row in process memory).
///
/// <para><b>New table (migration 0016), not saved_addresses (0006).</b>
/// <c>saved_addresses</c> already exists but backs a different, unrelated feature —
/// <c>IUsersStore.ListAddressesAsync</c> / <see cref="SavedAddress"/>
/// (still on <c>InMemoryUsersStore</c>) — and its shape does not fit this store even
/// setting ownership aside: its <c>user_id</c> is a UUID <c>NOT NULL REFERENCES
/// users(id)</c>, but <see cref="ISavedLocationStore"/>'s userId is an opaque
/// claim/header string that is never validated as a <c>users.id</c> UUID (see
/// <c>SavedLocationsController.TryGetUserId</c>); and its <c>line1</c> address column
/// is <c>NOT NULL</c> with a non-blank CHECK, but
/// <see cref="CreateSavedLocationRequest.Address"/> carries no <c>[Required]</c> —
/// the already-passing <c>SavedLocationsEndpointTests.Create_Persists_And_Is_Listable</c>
/// creates a location with no address at all, which a NOT NULL line1 would reject.
/// This store instead uses <c>saved_locations</c> (migration 0016), shaped like the
/// sibling durability migration <c>device_tokens</c> (0017): <c>user_id TEXT</c>, no
/// FK, no assumption about upstream identity format, and no label-uniqueness
/// constraint (the in-memory store never enforced unique labels per user, so adding
/// one would be a behaviour regression rather than the required strict superset).
/// </para>
///
/// <para><b>Ids are client-supplied, unlike Settlement's.</b> A
/// <see cref="JeebGateway.Financials.Settlement"/> id is always an internally
/// generated, well-formed GUID by the time it reaches
/// <c>PostgresSettlementStore</c>. A <see cref="SavedLocation"/> id is a client-supplied
/// URL segment (<c>GET/PUT/PATCH/DELETE .../saved-locations/{id}</c>), and the existing
/// endpoint tests assert that an unknown/malformed id (e.g. <c>"does-not-exist"</c>,
/// <c>"nope"</c>) resolves to a plain 404, never a 500. <see cref="TryParseId"/> guards
/// every by-id lookup so a non-GUID string returns "not found" instead of throwing —
/// matching <see cref="InMemorySavedLocationStore"/>'s plain dictionary-miss semantics
/// exactly.</para>
///
/// <para><b>REQ-02 "exactly one default".</b> Enforced at rest via the partial unique
/// index <c>uq_saved_locations_one_default_per_user</c>, plus a clear-then-set sequence
/// run inside one transaction per write that promotes a row to default (mirrors the
/// UPDATE-then-INSERT pattern documented on <c>saved_addresses</c>). Two edge cases the
/// naive in-memory port would get wrong under multi-replica concurrency are handled
/// explicitly:
/// <list type="bullet">
///   <item><see cref="UpdateAsync"/> rolls the whole transaction back — undoing any
///   default-clear it already issued — when the target id doesn't resolve, so a
///   not-found update can never silently strip a user's real default as a side
///   effect.</item>
///   <item><see cref="CreateAsync"/> retries once as a non-default row if it loses a
///   cross-replica race to become a brand-new user's implicit first default (the
///   partial unique index rejects the loser's insert) — the in-memory store's
///   per-user <c>lock</c> made this race impossible in a single process, but the
///   INSERT must not surface a raw 500 now that state lives in one shared database
///   behind possibly many gateway replicas.</item>
/// </list>
/// </para>
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

    public async Task<IReadOnlyList<SavedLocation>> ListAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // Mirrors InMemorySavedLocationStore's OrderByDescending(IsDefault).ThenBy(CreatedAt).
        const string sql = """
            SELECT * FROM saved_locations
            WHERE user_id = @UserId
            ORDER BY is_default DESC, created_at ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        return await ReadListAsync(cmd, ct);
    }

    public async Task<SavedLocation?> GetAsync(string userId, string id, CancellationToken ct)
    {
        if (!TryParseId(id, out var guid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM saved_locations WHERE id = @Id AND user_id = @UserId LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", guid);
        cmd.Parameters.AddWithValue("UserId", userId);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public Task<SavedLocation> CreateAsync(string userId, CreateSavedLocationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateCoreAsync(userId, request, forceNonDefault: false, ct);
    }

    public async Task<SavedLocation?> UpdateAsync(string userId, string id, UpdateSavedLocationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!TryParseId(id, out var guid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        if (request.IsDefault is true)
        {
            // REQ-02: promoting this row to default must first clear whichever row
            // currently holds it, or the partial unique index rejects the UPDATE
            // below (mirrors InMemorySavedLocationStore.ClearDefaults).
            await ClearDefaultsAsync(conn, tx, userId, ct);
        }

        // COALESCE(@Param, column) leaves a field untouched whenever the caller
        // didn't supply it — request.Address (etc.) is null both when the field was
        // omitted and when it was sent as JSON null, exactly matching the in-memory
        // store's `if (request.X is { } v) existing.X = v;` pattern. IsDefault is
        // tri-state (null = don't touch, true/false = set) and COALESCE handles that
        // the same way once bound as a nullable bool.
        const string sql = """
            UPDATE saved_locations
            SET label      = COALESCE(@Label, label),
                address    = COALESCE(@Address, address),
                latitude   = COALESCE(@Latitude, latitude),
                longitude  = COALESCE(@Longitude, longitude),
                is_default = COALESCE(@IsDefault, is_default),
                updated_at = now()
            WHERE id = @Id AND user_id = @UserId
            RETURNING *
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("Id", guid);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Label", (object?)request.Label ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Address", (object?)request.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Latitude", (object?)request.Latitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Longitude", (object?)request.Longitude ?? DBNull.Value);
        cmd.Parameters.AddWithValue("IsDefault", (object?)request.IsDefault ?? DBNull.Value);

        var rows = await ReadListAsync(cmd, ct);
        if (rows.Count == 0)
        {
            // No row for this (id, userId) — roll back so a not-found update never
            // leaves the earlier default-clear applied as a silent side effect.
            await tx.RollbackAsync(ct);
            return null;
        }

        await tx.CommitAsync(ct);
        return rows[0];
    }

    public async Task<bool> DeleteAsync(string userId, string id, CancellationToken ct)
    {
        if (!TryParseId(id, out var guid)) return false;

        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        const string deleteSql = """
            DELETE FROM saved_locations
            WHERE id = @Id AND user_id = @UserId
            RETURNING is_default
            """;
        await using var deleteCmd = new NpgsqlCommand(deleteSql, conn, tx);
        deleteCmd.Parameters.AddWithValue("Id", guid);
        deleteCmd.Parameters.AddWithValue("UserId", userId);

        bool? wasDefault = null;
        await using (var reader = await deleteCmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                wasDefault = reader.GetBoolean(0);
        }

        if (wasDefault is null)
        {
            await tx.RollbackAsync(ct);
            return false;
        }

        if (wasDefault.Value)
        {
            // REQ-02: a user always has a "my location" while any saved location
            // exists — promote the oldest remaining (mirrors
            // InMemorySavedLocationStore's OrderBy(CreatedAt).FirstOrDefault()). The
            // row we just deleted held the only is_default=TRUE slot for this user,
            // so this can never collide with the partial unique index.
            const string promoteSql = """
                UPDATE saved_locations
                SET is_default = TRUE, updated_at = now()
                WHERE id = (
                    SELECT id FROM saved_locations
                    WHERE user_id = @UserId
                    ORDER BY created_at ASC
                    LIMIT 1
                )
                """;
            await using var promoteCmd = new NpgsqlCommand(promoteSql, conn, tx);
            promoteCmd.Parameters.AddWithValue("UserId", userId);
            await promoteCmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return true;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<SavedLocation> CreateCoreAsync(
        string userId, CreateSavedLocationRequest request, bool forceNonDefault, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // REQ-02: the first-ever saved location for a user is the implicit default,
        // even when the caller didn't ask for it (mirrors InMemorySavedLocationStore:
        // `request.IsDefault || bucket.Count == 0`).
        var hasExisting = await HasAnyAsync(conn, tx, userId, ct);
        var makeDefault = !forceNonDefault && (request.IsDefault || !hasExisting);

        if (makeDefault)
            await ClearDefaultsAsync(conn, tx, userId, ct);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        const string insertSql = """
            INSERT INTO saved_locations (
                id, user_id, label, address, latitude, longitude, is_default, created_at, updated_at
            ) VALUES (
                @Id, @UserId, @Label, @Address, @Latitude, @Longitude, @IsDefault, @CreatedAt, @UpdatedAt
            )
            RETURNING *
            """;
        await using var cmd = new NpgsqlCommand(insertSql, conn, tx);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Label", request.Label);
        cmd.Parameters.AddWithValue("Address", (object?)request.Address ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Latitude", request.Latitude);
        cmd.Parameters.AddWithValue("Longitude", request.Longitude);
        cmd.Parameters.AddWithValue("IsDefault", makeDefault);
        cmd.Parameters.AddWithValue("CreatedAt", now);
        cmd.Parameters.AddWithValue("UpdatedAt", now);

        List<SavedLocation> rows;
        try
        {
            rows = await ReadListAsync(cmd, ct);
        }
        catch (PostgresException ex) when (
            ex.SqlState == PostgresErrorCodes.UniqueViolation
            && ex.ConstraintName == "uq_saved_locations_one_default_per_user"
            && makeDefault && !forceNonDefault)
        {
            // Lost a cross-replica race to become this user's default
            // (uq_saved_locations_one_default_per_user) — another request already
            // claimed it between our HasAnyAsync check and this INSERT. Retry once,
            // forced non-default; makeDefault is then unconditionally false, so this
            // branch cannot be hit again and the retry cannot loop.
            await tx.RollbackAsync(ct);
            _log.LogDebug(
                "saved location create lost default race for userId={UserId}; retrying as non-default", userId);
            return await CreateCoreAsync(userId, request, forceNonDefault: true, ct);
        }

        await tx.CommitAsync(ct);
        _log.LogInformation(
            "Saved location created id={Id} userId={UserId} isDefault={IsDefault}", id, userId, makeDefault);
        return rows[0];
    }

    private static async Task<bool> HasAnyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        const string sql = "SELECT EXISTS(SELECT 1 FROM saved_locations WHERE user_id = @UserId)";
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task ClearDefaultsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string userId, CancellationToken ct)
    {
        const string sql = """
            UPDATE saved_locations
            SET is_default = FALSE, updated_at = now()
            WHERE user_id = @UserId AND is_default = TRUE
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// SavedLocation ids are client-supplied path segments (unlike Settlement's
    /// internally generated ids), so a malformed/unknown id must resolve to "not
    /// found" rather than throw a <see cref="FormatException"/> — see
    /// SavedLocationsEndpointTests.Update_Unknown_Id_Returns_404 /
    /// Delete_Unknown_Id_Returns_404, which pass non-GUID strings.
    /// </summary>
    private static bool TryParseId(string id, out Guid guid) => Guid.TryParse(id, out guid);

    private static async Task<List<SavedLocation>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<SavedLocation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static SavedLocation MapRow(NpgsqlDataReader r) => new()
    {
        Id        = r.GetGuid(r.GetOrdinal("id")).ToString(),
        UserId    = r.GetString(r.GetOrdinal("user_id")),
        Label     = r.GetString(r.GetOrdinal("label")),
        Address   = r.IsDBNull(r.GetOrdinal("address")) ? null : r.GetString(r.GetOrdinal("address")),
        Latitude  = r.GetDouble(r.GetOrdinal("latitude")),
        Longitude = r.GetDouble(r.GetOrdinal("longitude")),
        IsDefault = r.GetBoolean(r.GetOrdinal("is_default")),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at")),
    };
}
