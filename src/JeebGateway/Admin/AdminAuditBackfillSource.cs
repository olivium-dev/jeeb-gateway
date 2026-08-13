using JeebGateway.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Admin;

// gwdbx W1-04 — read seam over admin_actions for the one-shot relay. Keyset-paged on
// (created_at, id) so a page boundary can neither skip nor duplicate a row.
public interface IAdminAuditBackfillSource
{
    Task<IReadOnlyList<AdminAuditEntry>> ReadPageAsync(
        AdminAuditCursor? after, int limit, CancellationToken ct);

    Task<long> CountAsync(CancellationToken ct);
}

/// <summary>Keyset position: the last relayed row's (created_at, id).</summary>
public readonly record struct AdminAuditCursor(DateTimeOffset CreatedAt, Guid Id);

/// <summary>
/// Reads admin_actions straight off GatewayPostgres with the same raw-Npgsql shape and row
/// mapping as <see cref="PostgresAdminAuditLog"/>, so the relayed body matches the mirror's.
/// </summary>
public sealed class PostgresAdminAuditBackfillSource : IAdminAuditBackfillSource
{
    private readonly INpgsqlConnectionFactory _db;

    public PostgresAdminAuditBackfillSource(INpgsqlConnectionFactory db) => _db = db;

    public async Task<long> CountAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM admin_actions", conn);
        return (long)(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<IReadOnlyList<AdminAuditEntry>> ReadPageAsync(
        AdminAuditCursor? after, int limit, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // (created_at, id) row-value comparison keeps same-timestamp rows in a total order.
        var sql = after is null
            ? """
              SELECT * FROM admin_actions
              ORDER BY created_at, id
              LIMIT @Limit
              """
            : """
              SELECT * FROM admin_actions
              WHERE (created_at, id) > (@AfterCreatedAt, @AfterId)
              ORDER BY created_at, id
              LIMIT @Limit
              """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Limit", limit);
        if (after is { } cursor)
        {
            cmd.Parameters.Add(new NpgsqlParameter("AfterCreatedAt", NpgsqlDbType.TimestampTz)
            {
                Value = cursor.CreatedAt
            });
            cmd.Parameters.AddWithValue("AfterId", cursor.Id);
        }

        var rows = new List<AdminAuditEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(PostgresAdminAuditLog.MapRow(reader));
        }

        return rows;
    }
}
