using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.ProhibitedItems;

/// <summary>
/// Postgres-backed <see cref="IProhibitedItemsStore"/> (jeeb-gateway durability
/// hardening). Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s
/// raw-Npgsql shape (connection-per-call via <see cref="INpgsqlConnectionFactory"/>,
/// named parameters, reader-based row mapping via a shared <c>MapRow</c>/
/// <c>ReadListAsync</c> pair). Replaces <see cref="InMemoryProhibitedItemsStore"/> in
/// production so the moderated catalog (<see cref="ModerationGate"/>, the create-time
/// gate in RequestsController, the admin CRUD/bulk-import surface) and the per-user
/// acknowledgment ledger survive a gateway bounce / replica move.
///
/// <para><b>Table reuse (never re-CREATE).</b> The catalog persists to the EXISTING
/// <c>prohibited_items</c> table (migration 0005) — <c>id</c>, <c>name</c>,
/// <c>category</c>, <c>description</c>, <c>active</c>, <c>created_by</c>,
/// <c>updated_by</c>, <c>created_at</c>, <c>updated_at</c> are reused as-is, including
/// the existing <c>prohibited_items_name_lower_uniq</c> case-insensitive unique index.
/// Migration 0018 ALTERs the table to add the one column 0005 never carried:
/// <c>severity</c> (JEB-63). <see cref="ProhibitedItem.Severity"/> / <see
/// cref="ProhibitedItemCreate.Severity"/> have existed at the application layer since
/// JEB-63 shipped, but every environment that only ever ran 0005 + the 0011 seed has
/// never persisted it — <see cref="InMemoryProhibitedItemsStore"/> was the only place
/// it actually lived. The new column defaults to <c>'block'</c>, matching
/// <see cref="ProhibitedItem.Severity"/>'s C# default exactly, so every pre-existing
/// row keeps the stricter hard-reject classification after the ALTER backfills it.</para>
///
/// <para><b>Duplicate-name rejection, not idempotent upsert.</b> Unlike
/// <see cref="JeebGateway.Financials.PostgresSettlementStore.TryInsertAsync"/>'s
/// insert-ONCE-then-return-existing-row shape, <see cref="IProhibitedItemsStore.CreateAsync"/>
/// / <see cref="IProhibitedItemsStore.UpdateAsync"/> must REJECT a name collision with
/// <see cref="DuplicateProhibitedItemNameException"/> — that is the actual domain
/// contract <see cref="InMemoryProhibitedItemsStore"/> implements (case-insensitive,
/// checked against active AND inactive rows alike). This store lets the existing
/// <c>prohibited_items_name_lower_uniq</c> unique index be the single source of truth
/// for that invariant (race-free under concurrent admin writes, unlike an app-level
/// check-then-insert) and translates the resulting <see cref="PostgresException"/>
/// (SQLSTATE 23505 on that specific index) into the domain exception at the call site.</para>
///
/// <para><b><c>created_by</c> / <c>updated_by</c> are best-effort, not guaranteed.</b>
/// The column is <c>UUID NULL REFERENCES users(id) ON DELETE SET NULL</c> (0005), but
/// the <c>adminUserId</c> string handed to <see cref="CreateAsync"/> / <see
/// cref="UpdateAsync"/> is NOT always a UUID — <see cref="DefaultLexiconSeeder"/> calls
/// in with the literal sentinel <c>"system:lexicon-seed"</c>, and
/// <c>JeebGateway.Users.UserIdentity.TryGetUserId</c> can also resolve to an
/// X-User-Id header value of arbitrary shape. <see cref="InMemoryProhibitedItemsStore"/>
/// has no such constraint (any string goes). To preserve that behaviour instead of
/// throwing on every non-UUID caller, <see cref="ParseUserIdOrNull"/> stores the
/// parsed UUID when possible and NULL otherwise — a graceful degrade, not a crash.</para>
///
/// <para><b>Acknowledgment ledger — <c>prohibited_item_acks</c> (migration 0018,
/// NEW table).</b> The real ack contract is "user acknowledged the ACTIVE LEXICON AS
/// OF VERSION V" (<see cref="ModerationGate.ComputeLexiconVersion"/> /
/// <c>ProhibitedItemsController.ComputeVersion</c> — the max <c>updated_at</c> across
/// the active catalog, rendered "O"-format, or the literal string <c>"empty"</c>), NOT
/// "user acknowledged catalog item X" — <see cref="UserAcknowledgment"/> carries a
/// <c>Version</c> string, never an item id. The child table's second key column is
/// therefore <c>lexicon_version TEXT</c> (see migration 0018's header for the full
/// rationale) rather than a UUID FK to a single <c>prohibited_items</c> row, which the
/// version token can never be. <see cref="GetAcknowledgmentAsync"/> resolves the
/// newest row per user (<c>ORDER BY acknowledged_at DESC LIMIT 1</c>), reproducing
/// <see cref="InMemoryProhibitedItemsStore"/>'s "last ack wins" read exactly, while
/// <see cref="AcknowledgeAsync"/> upserts on <c>(user_id, lexicon_version)</c> so a
/// double-tap / retry on the SAME version is idempotent (refreshes
/// <c>acknowledged_at</c>) instead of erroring.</para>
/// </summary>
public sealed class PostgresProhibitedItemsStore : IProhibitedItemsStore
{
    private const string NameUniqueConstraint = "prohibited_items_name_lower_uniq";

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresProhibitedItemsStore> _log;

    public PostgresProhibitedItemsStore(INpgsqlConnectionFactory db, ILogger<PostgresProhibitedItemsStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<IReadOnlyList<ProhibitedItem>> ListActiveAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM prohibited_items
            WHERE active = TRUE
            ORDER BY LOWER(category) ASC, LOWER(name) ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await ReadListAsync(cmd, ct);
    }

    public async Task<ProhibitedItemsPage> ListAllAsync(int page, int pageSize, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string countSql = "SELECT COUNT(*) FROM prohibited_items";
        await using var countCmd = new NpgsqlCommand(countSql, conn);
        var total = Convert.ToInt32(await countCmd.ExecuteScalarAsync(ct));

        // Mirrors InMemoryProhibitedItemsStore's `(page - 1) * pageSize`; clamped to
        // zero so an out-of-contract page/pageSize (the controller already validates
        // page >= 1, but the store interface itself does not) can never reach Postgres
        // as a negative OFFSET, which — unlike LINQ's forgiving Skip() — is a hard
        // SQL error.
        var skip = Math.Max(0, (page - 1) * pageSize);

        const string pageSql = """
            SELECT * FROM prohibited_items
            ORDER BY updated_at DESC
            OFFSET @Skip LIMIT @PageSize
            """;
        await using var cmd = new NpgsqlCommand(pageSql, conn);
        cmd.Parameters.AddWithValue("Skip", skip);
        cmd.Parameters.AddWithValue("PageSize", pageSize);
        var items = await ReadListAsync(cmd, ct);

        return new ProhibitedItemsPage { Items = items, Total = total };
    }

    public async Task<ProhibitedItem?> GetAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var itemId)) return null;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM prohibited_items WHERE id = @Id LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", itemId);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<ProhibitedItem> CreateAsync(ProhibitedItemCreate input, string adminUserId, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var name = input.Name.Trim(); // matches InMemoryProhibitedItemsStore's item.Name.Trim()

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO prohibited_items (
                id, name, category, description, severity, active,
                created_by, updated_by, created_at, updated_at
            ) VALUES (
                @Id, @Name, @Category, @Description, @Severity, TRUE,
                @CreatedBy, @UpdatedBy, now(), now()
            )
            RETURNING *
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Name", name);
        cmd.Parameters.AddWithValue("Category", input.Category);
        cmd.Parameters.AddWithValue("Description", (object?)input.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Severity", ToWireSeverity(input.Severity));
        cmd.Parameters.AddWithValue("CreatedBy", ParseUserIdOrNull(adminUserId));
        cmd.Parameters.AddWithValue("UpdatedBy", ParseUserIdOrNull(adminUserId));

        try
        {
            var rows = await ReadListAsync(cmd, ct);
            var created = rows[0];
            _log.LogInformation(
                "Prohibited item created id={Id} name={Name} category={Category} severity={Severity}",
                created.Id, created.Name, created.Category, created.Severity);
            return created;
        }
        catch (PostgresException ex) when (IsNameUniqueViolation(ex))
        {
            throw new DuplicateProhibitedItemNameException(name);
        }
    }

    public async Task<ProhibitedItem?> UpdateAsync(string id, ProhibitedItemPatch patch, string adminUserId, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var itemId)) return null;

        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.Parameters.AddWithValue("Id", itemId);
        cmd.Parameters.AddWithValue("UpdatedBy", ParseUserIdOrNull(adminUserId));

        var setClauses = new List<string> { "updated_by = @UpdatedBy", "updated_at = now()" };

        // Same partial-patch semantics as InMemoryProhibitedItemsStore.UpdateAsync:
        // null means "leave unchanged" for every field except Active (a genuine
        // bool?), and Description can be set to a new value but never explicitly
        // cleared back to null via a patch.
        string? trimmedName = null;
        if (patch.Name is not null)
        {
            trimmedName = patch.Name.Trim();
            setClauses.Add("name = @Name");
            cmd.Parameters.AddWithValue("Name", trimmedName);
        }
        if (patch.Category is not null)
        {
            setClauses.Add("category = @Category");
            cmd.Parameters.AddWithValue("Category", patch.Category);
        }
        if (patch.Description is not null)
        {
            setClauses.Add("description = @Description");
            cmd.Parameters.AddWithValue("Description", patch.Description);
        }
        if (patch.Severity is { } severity)
        {
            setClauses.Add("severity = @Severity");
            cmd.Parameters.AddWithValue("Severity", ToWireSeverity(severity));
        }
        if (patch.Active is { } active)
        {
            setClauses.Add("active = @Active");
            cmd.Parameters.AddWithValue("Active", active);
        }

        cmd.CommandText = $"""
            UPDATE prohibited_items
            SET {string.Join(", ", setClauses)}
            WHERE id = @Id
            RETURNING *
            """;

        try
        {
            var rows = await ReadListAsync(cmd, ct);
            if (rows.Count == 0) return null;

            var updated = rows[0];
            _log.LogInformation("Prohibited item updated id={Id} name={Name}", updated.Id, updated.Name);
            return updated;
        }
        catch (PostgresException ex) when (IsNameUniqueViolation(ex))
        {
            throw new DuplicateProhibitedItemNameException(trimmedName ?? id);
        }
    }

    public async Task<UserAcknowledgment?> GetAcknowledgmentAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT user_id, lexicon_version, acknowledged_at
            FROM prohibited_item_acks
            WHERE user_id = @UserId
            ORDER BY acknowledged_at DESC
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? MapAck(reader) : null;
    }

    public async Task<UserAcknowledgment> AcknowledgeAsync(string userId, string version, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO prohibited_item_acks (user_id, lexicon_version, acknowledged_at)
            VALUES (@UserId, @Version, now())
            ON CONFLICT (user_id, lexicon_version) DO UPDATE
                SET acknowledged_at = now()
            RETURNING user_id, lexicon_version, acknowledged_at
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("Version", version);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var ack = MapAck(reader);

        _log.LogInformation(
            "Prohibited-items lexicon acknowledged userId={UserId} version={Version}",
            userId, version);
        return ack;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsNameUniqueViolation(PostgresException ex) =>
        ex.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(ex.ConstraintName, NameUniqueConstraint, StringComparison.Ordinal);

    /// <summary>
    /// <c>created_by</c> / <c>updated_by</c> are UUID-typed FKs onto <c>users(id)</c>
    /// (0005), but callers are not guaranteed to hand back a UUID (see class remarks —
    /// <see cref="DefaultLexiconSeeder"/>'s <c>"system:lexicon-seed"</c> sentinel).
    /// Best effort: store the parsed UUID when possible, NULL otherwise, rather than
    /// throwing and taking the whole write down.
    /// </summary>
    private static object ParseUserIdOrNull(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && Guid.TryParse(userId, out var guid)
            ? guid
            : DBNull.Value;

    private static string ToWireSeverity(ProhibitedSeverity severity) =>
        severity == ProhibitedSeverity.Warn ? "warn" : "block";

    /// <summary>Unrecognised text defaults to Block — same fail-safe posture as
    /// <see cref="ProhibitedItem.Severity"/>'s own C# default.</summary>
    private static ProhibitedSeverity ParseSeverity(string wire) =>
        string.Equals(wire, "warn", StringComparison.OrdinalIgnoreCase)
            ? ProhibitedSeverity.Warn
            : ProhibitedSeverity.Block;

    private static UserAcknowledgment MapAck(NpgsqlDataReader r) => new()
    {
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Version = r.GetString(r.GetOrdinal("lexicon_version")),
        AcknowledgedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("acknowledged_at"))
    };

    private static async Task<List<ProhibitedItem>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<ProhibitedItem>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static ProhibitedItem MapRow(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(r.GetOrdinal("id")).ToString(),
        Name = r.GetString(r.GetOrdinal("name")),
        Category = r.GetString(r.GetOrdinal("category")),
        Description = r.IsDBNull(r.GetOrdinal("description")) ? null : r.GetString(r.GetOrdinal("description")),
        Severity = ParseSeverity(r.GetString(r.GetOrdinal("severity"))),
        Active = r.GetBoolean(r.GetOrdinal("active")),
        CreatedBy = r.IsDBNull(r.GetOrdinal("created_by")) ? null : r.GetGuid(r.GetOrdinal("created_by")).ToString(),
        UpdatedBy = r.IsDBNull(r.GetOrdinal("updated_by")) ? null : r.GetGuid(r.GetOrdinal("updated_by")).ToString(),
        CreatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
        UpdatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("updated_at"))
    };
}
