using System.Text.Json;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Admin;

/// <summary>
/// Postgres-backed <see cref="IAdminAuditLog"/> (T-backend-030, gateway
/// durability hardening).
///
/// Replaces <see cref="InMemoryAdminAuditLog"/> in production so the
/// admin-mutation audit trail survives a gateway bounce and is visible across
/// replicas. Reuses the existing <c>admin_actions</c> table
/// (db/migrations/0005) as-is — no new table, no migration. Mirrors
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s raw-Npgsql
/// shape (same connection factory, param binding, and
/// ReadListAsync/MapRow reader helpers) plus the JSONB-column idiom
/// (System.Text.Json + <see cref="NpgsqlDbType.Jsonb"/>) and "malformed-id
/// guard" established by the sibling stores from the same hardening pass:
/// <see cref="JeebGateway.ProhibitedItems.FlaggedRequests.PostgresFlaggedRequestStore"/>
/// and <see cref="JeebGateway.Requests.OtpHandover.PostgresAdminEscalationStore"/>.
///
/// <para><b>No uniqueness invariant.</b> <c>admin_actions</c> is append-only
/// by design (migration 0005 remarks: "INSERT-only by convention ... no
/// UPDATE/DELETE path") with no unique constraint beyond the generated PK, so
/// <see cref="AppendAsync"/> is a plain INSERT with no ON CONFLICT branch —
/// every admin mutation gets its own row, matching
/// <see cref="InMemoryAdminAuditLog"/>'s always-append
/// <c>ConcurrentQueue.Enqueue</c> semantics.</para>
///
/// <para><b>Non-UUID entity ids.</b> <c>entity_id</c> is UUID-typed (migration
/// 0005 comment: "UUID of the touched row when available"). A few entity
/// types mint ids that are not GUID-shaped (e.g. <c>DisputeService</c>'s
/// <c>dsp_&lt;hex&gt;</c> ids and <c>DisputeCaseService</c>'s
/// <c>case_&lt;hex&gt;</c> ids). For those, <see cref="AppendAsync"/> still
/// records the row in full — admin_user_id, action, entity_type, before/after
/// JSONB are never lost — but leaves the native <c>entity_id</c> column NULL
/// instead of letting <c>Guid.Parse</c> throw, exactly like
/// <see cref="JeebGateway.ProhibitedItems.FlaggedRequests.PostgresFlaggedRequestStore"/>'s
/// documented "malformed-id guard" (TryParse, degrade gracefully, never
/// 500). <see cref="ListForEntityAsync"/> mirrors the same guard on read: a
/// non-GUID <c>entityId</c> can never match the UUID column, so it returns an
/// empty timeline instead of throwing.</para>
///
/// <para><b>admin_user_id is NOT NULL and FK-constrained</b> (<c>REFERENCES
/// users(id) ON DELETE RESTRICT</c>), so unlike <c>entity_id</c> it is always
/// parsed with a trusting <c>Guid.Parse</c> (mirroring
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s own
/// unguarded <c>Guid.Parse(settlement.Id)</c> for its NOT-NULL UUID PK).
/// Every current caller resolves this value via
/// <see cref="JeebGateway.Users.UserIdentity.TryGetUserId"/>, which is
/// documented to always resolve to the gateway's own user GUID.</para>
/// </summary>
public sealed class PostgresAdminAuditLog : IAdminAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresAdminAuditLog> _log;

    public PostgresAdminAuditLog(INpgsqlConnectionFactory db, ILogger<PostgresAdminAuditLog> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<AdminAuditEntry> AppendAsync(AdminAuditAppend entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO admin_actions (
                id, admin_user_id, action, entity_type, entity_id,
                before_state, after_state, request_id
            ) VALUES (
                @Id, @AdminUserId, @Action, @EntityType, @EntityId,
                @BeforeState, @AfterState, @RequestId
            )
            RETURNING *
            """;

        // entity_id is UUID-typed but not every entity type mints GUID-shaped
        // ids (see class remarks) — degrade to NULL rather than throw.
        Guid? entityGuid = Guid.TryParse(entry.EntityId, out var parsedEntityId) ? parsedEntityId : null;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.NewGuid());
        insertCmd.Parameters.AddWithValue("AdminUserId", Guid.Parse(entry.AdminUserId));
        insertCmd.Parameters.AddWithValue("Action", entry.Action);
        insertCmd.Parameters.AddWithValue("EntityType", entry.EntityType);
        insertCmd.Parameters.Add(new NpgsqlParameter("EntityId", NpgsqlDbType.Uuid)
        {
            Value = (object?)entityGuid ?? DBNull.Value
        });
        insertCmd.Parameters.Add(new NpgsqlParameter("BeforeState", NpgsqlDbType.Jsonb)
        {
            Value = SerializeState(entry.BeforeState)
        });
        insertCmd.Parameters.Add(new NpgsqlParameter("AfterState", NpgsqlDbType.Jsonb)
        {
            Value = SerializeState(entry.AfterState)
        });
        insertCmd.Parameters.AddWithValue("RequestId", (object?)entry.RequestId ?? DBNull.Value);

        var rows = await ReadListAsync(insertCmd, ct);
        var row = rows[0];

        _log.LogInformation(
            "Admin action recorded id={Id} adminUserId={AdminUserId} action={Action} entityType={EntityType} entityId={EntityId}",
            row.Id, row.AdminUserId, row.Action, row.EntityType, row.EntityId);

        return row;
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ListForEntityAsync(
        string entityType, string entityId, CancellationToken ct)
    {
        // Malformed-id guard (mirrors PostgresFlaggedRequestStore.GetAsync): a
        // non-GUID entityId can never match the UUID-typed column, so miss
        // gracefully instead of letting Guid.Parse throw — matching
        // InMemoryAdminAuditLog's plain-equality "no match" semantics for a
        // key shape entity_id could never have stored natively anyway.
        if (!Guid.TryParse(entityId, out var guid))
        {
            return Array.Empty<AdminAuditEntry>();
        }

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM admin_actions
            WHERE entity_type = @EntityType AND entity_id = @EntityId
            ORDER BY created_at DESC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("EntityType", entityType);
        cmd.Parameters.AddWithValue("EntityId", guid);

        return await ReadListAsync(cmd, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<List<AdminAuditEntry>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<AdminAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static AdminAuditEntry MapRow(NpgsqlDataReader r)
    {
        return new AdminAuditEntry
        {
            Id = r.GetGuid(r.GetOrdinal("id")).ToString(),
            AdminUserId = r.GetGuid(r.GetOrdinal("admin_user_id")).ToString(),
            Action = r.GetString(r.GetOrdinal("action")),
            EntityType = r.GetString(r.GetOrdinal("entity_type")),
            EntityId = r.IsDBNull(r.GetOrdinal("entity_id"))
                ? null
                : r.GetGuid(r.GetOrdinal("entity_id")).ToString(),
            BeforeState = DeserializeState(r, "before_state"),
            AfterState = DeserializeState(r, "after_state"),
            RequestId = r.IsDBNull(r.GetOrdinal("request_id"))
                ? null
                : r.GetString(r.GetOrdinal("request_id")),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
        };
    }

    private static object SerializeState(IReadOnlyDictionary<string, object?>? state) =>
        state is null ? (object)DBNull.Value : JsonSerializer.Serialize(state, Json);

    private static IReadOnlyDictionary<string, object?>? DeserializeState(NpgsqlDataReader r, string column)
    {
        var ordinal = r.GetOrdinal(column);
        if (r.IsDBNull(ordinal)) return null;

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(r.GetString(ordinal), Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
