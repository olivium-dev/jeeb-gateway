using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JeebGateway.Users.DataExport;

/// <summary>
/// Postgres-backed <see cref="IDataExportStore"/> (T-backend-042, GDPR-like right of
/// access).
///
/// Replaces <see cref="InMemoryDataExportStore"/> in production. A queued/processing/
/// ready row — and the packaged payload bytes behind its single-use download link — now
/// survive a gateway bounce or a replica move instead of evaporating mid-SLA, and
/// <see cref="ClaimNextAsync"/> is safe across concurrent gateway replicas (it was only
/// safe within a single process before, via the in-memory store's <c>lock</c>).
///
/// <para><b>Idempotency.</b> <see cref="RequestAsync"/> mirrors
/// <see cref="JeebGateway.Financials.PostgresSettlementStore.TryInsertAsync"/>'s shape exactly:
/// <c>INSERT ... ON CONFLICT ... DO NOTHING</c> against a DB-level uniqueness
/// constraint, then a fallback read of the existing row when the insert loses the
/// race. Here the arbiter is a partial UNIQUE index on <c>(user_id)</c> restricted to
/// the open lifecycle states (<c>migrations/0023_init_data_exports.sql</c>) — the
/// Postgres equivalent of the in-memory store's "does this user already have an open
/// export" scan — so a retried POST never double-queues even across replicas.</para>
///
/// <para><b>Single-use download tokens.</b> The token is minted via
/// <see cref="InMemoryDataExportStore.MintToken"/> (reused as-is — same entropy source,
/// same URL-safe encoding, both stores hand out byte-for-byte comparable tokens).
/// <see cref="MarkDeliveredAsync"/> flips <c>token_used = TRUE</c> in the SAME
/// <c>UPDATE ... WHERE status = 'ready'</c> statement that transitions the row to
/// <c>delivered</c>, so two racing downloads of the same token can never both
/// succeed: the loser's UPDATE matches zero rows because the winner already moved the
/// row out of <c>ready</c>.</para>
///
/// <para><b>SLA enforcement.</b> <see cref="ListOverdueOpenAsync"/> is additional
/// surface (not part of <see cref="IDataExportStore"/>) consumed only by
/// <see cref="DataExportWorker"/>'s sweep — the in-memory store never had SLA-breach
/// detection either, so this is additive hardening, not a behavioural change to the
/// existing in-memory path.</para>
/// </summary>
public sealed class PostgresDataExportStore : IDataExportStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly TimeProvider _clock;
    private readonly IOptions<DataExportOptions> _options;
    private readonly ILogger<PostgresDataExportStore> _log;

    public PostgresDataExportStore(
        INpgsqlConnectionFactory db,
        TimeProvider clock,
        IOptions<DataExportOptions> options,
        ILogger<PostgresDataExportStore> log)
    {
        _db = db;
        _clock = clock;
        _options = options;
        _log = log;
    }

    public async Task<DataExportRequest> RequestAsync(string userId, string format, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var record = new DataExportRequest
        {
            Id = Guid.NewGuid().ToString(),
            UserId = userId,
            Status = DataExportStatus.Queued,
            Format = format,
            RequestedAt = now,
            DueBy = now + _options.Value.Sla
        };

        await using var conn = await _db.OpenAsync(ct);

        // Arbiter predicate below MUST stay textually identical to the partial
        // unique index in migrations/0023_init_data_exports.sql
        // (uq_data_exports_user_open) — Postgres infers the ON CONFLICT target
        // from a syntactic match against the index's predicate.
        const string insertSql = """
            INSERT INTO data_exports (
                id, user_id, status, format, requested_at, due_by, created_at, updated_at
            ) VALUES (
                @Id, @UserId, @Status, @Format, @RequestedAt, @DueBy, now(), now()
            )
            ON CONFLICT (user_id) WHERE status IN ('queued', 'processing', 'ready') DO NOTHING
            RETURNING id
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.Parse(record.Id));
        insertCmd.Parameters.AddWithValue("UserId", record.UserId);
        insertCmd.Parameters.AddWithValue("Status", record.Status);
        insertCmd.Parameters.AddWithValue("Format", record.Format);
        insertCmd.Parameters.AddWithValue("RequestedAt", record.RequestedAt);
        insertCmd.Parameters.AddWithValue("DueBy", record.DueBy);

        var inserted = await insertCmd.ExecuteScalarAsync(ct) is not null;

        if (inserted)
        {
            _log.LogInformation(
                "Data export {ExportId} queued for user {UserId} (dueBy={DueBy})",
                record.Id, userId, record.DueBy);
            return record;
        }

        // Conflict — an open export already exists for this user; return it
        // unchanged so a retried POST never double-queues.
        var existing = await GetOpenForUserAsync(userId, ct);
        _log.LogDebug("Data export already open for user {UserId}; returning existing row", userId);
        return existing!;
    }

    public async Task<DataExportRequest?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await QuerySingleAsync(
            conn, "WHERE user_id = @p ORDER BY requested_at DESC", "@p", userId, ct);
    }

    public async Task<DataExportRequest?> GetByDownloadTokenAsync(
        string token, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM data_exports
            WHERE download_token = @Token
              AND status = @Ready
              AND token_used = FALSE
              AND (expires_at IS NULL OR expires_at > @Now)
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Token", token);
        cmd.Parameters.AddWithValue("Ready", DataExportStatus.Ready);
        cmd.Parameters.AddWithValue("Now", now);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<DataExportRequest?> ClaimNextAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // FOR UPDATE SKIP LOCKED job-queue claim: safe across concurrent gateway
        // replicas, unlike the in-memory store's single-process lock it replaces.
        const string sql = """
            WITH next_export AS (
                SELECT id FROM data_exports
                WHERE status = @Queued
                ORDER BY requested_at ASC
                LIMIT 1
                FOR UPDATE SKIP LOCKED
            )
            UPDATE data_exports
            SET status = @Processing, started_at = @Now, updated_at = now()
            FROM next_export
            WHERE data_exports.id = next_export.id
            RETURNING data_exports.*
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Queued", DataExportStatus.Queued);
        cmd.Parameters.AddWithValue("Processing", DataExportStatus.Processing);
        cmd.Parameters.AddWithValue("Now", now);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<string> MarkReadyAsync(
        string exportId,
        byte[] payload,
        string contentType,
        DateTimeOffset now,
        TimeSpan linkValidity,
        CancellationToken ct)
    {
        var id = Guid.Parse(exportId);
        var token = InMemoryDataExportStore.MintToken();

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE data_exports
            SET status = @Ready,
                completed_at = @Now,
                download_token = @Token,
                expires_at = @ExpiresAt,
                payload = @Payload,
                payload_content_type = @ContentType,
                payload_size_bytes = @PayloadSize,
                updated_at = now()
            WHERE id = @Id AND status = @Processing
            RETURNING id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Ready", DataExportStatus.Ready);
        cmd.Parameters.AddWithValue("Processing", DataExportStatus.Processing);
        cmd.Parameters.AddWithValue("Now", now);
        cmd.Parameters.AddWithValue("Token", token);
        cmd.Parameters.AddWithValue("ExpiresAt", now + linkValidity);
        cmd.Parameters.AddWithValue("Payload", payload);
        cmd.Parameters.AddWithValue("ContentType", contentType);
        cmd.Parameters.AddWithValue("PayloadSize", payload.LongLength);

        var updated = await cmd.ExecuteScalarAsync(ct) is not null;
        if (updated)
        {
            _log.LogInformation(
                "Data export {ExportId} ready ({Bytes} bytes)", exportId, payload.LongLength);
            return token;
        }

        // Not updated — distinguish missing row from wrong-status so the exception
        // contract matches InMemoryDataExportStore.MarkReadyAsync exactly.
        var status = await GetStatusAsync(id, ct);
        if (status is null)
        {
            throw new InvalidOperationException($"Data export '{exportId}' not found.");
        }
        throw new InvalidOperationException(
            $"Data export '{exportId}' is in status '{status}', expected 'processing'.");
    }

    public async Task MarkFailedAsync(string exportId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE data_exports
            SET status = @Failed, failed_at = @Now, failure_reason = @Reason, payload = NULL, updated_at = now()
            WHERE id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Failed", DataExportStatus.Failed);
        cmd.Parameters.AddWithValue("Now", now);
        cmd.Parameters.AddWithValue("Reason", reason);
        cmd.Parameters.AddWithValue("Id", Guid.Parse(exportId));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> MarkDeliveredAsync(string exportId, DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // token_used flips TRUE in the same statement that checks status = 'ready',
        // so a racing second call (replayed token) matches zero rows and returns
        // false — the single-use guarantee holds even across gateway replicas.
        const string sql = """
            UPDATE data_exports
            SET status = @Delivered, delivered_at = @Now, token_used = TRUE, payload = NULL, updated_at = now()
            WHERE id = @Id AND status = @Ready
            RETURNING id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Delivered", DataExportStatus.Delivered);
        cmd.Parameters.AddWithValue("Now", now);
        cmd.Parameters.AddWithValue("Id", Guid.Parse(exportId));
        cmd.Parameters.AddWithValue("Ready", DataExportStatus.Ready);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    /// <summary>
    /// Additional to <see cref="IDataExportStore"/> — consumed only by
    /// <see cref="DataExportWorker"/>'s SLA sweep. Returns open (queued / processing)
    /// rows whose <see cref="DataExportRequest.DueBy"/> deadline has already passed,
    /// oldest breach first.
    /// </summary>
    public async Task<IReadOnlyList<DataExportRequest>> ListOverdueOpenAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM data_exports
            WHERE status = ANY(@OpenStatuses)
              AND due_by < @Now
            ORDER BY due_by ASC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(
            "OpenStatuses", new[] { DataExportStatus.Queued, DataExportStatus.Processing });
        cmd.Parameters.AddWithValue("Now", now);
        return await ReadListAsync(cmd, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<DataExportRequest?> GetOpenForUserAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT * FROM data_exports
            WHERE user_id = @UserId AND status IN ('queued', 'processing', 'ready')
            ORDER BY requested_at DESC
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task<string?> GetStatusAsync(Guid id, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT status FROM data_exports WHERE id = @Id";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private static async Task<DataExportRequest?> QuerySingleAsync(
        NpgsqlConnection conn, string whereClause, string paramName, object paramValue, CancellationToken ct)
    {
        var sql = $"SELECT * FROM data_exports {whereClause} LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(paramName, paramValue);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<List<DataExportRequest>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<DataExportRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static DataExportRequest MapRow(NpgsqlDataReader r) => new()
    {
        Id = r.GetGuid(r.GetOrdinal("id")).ToString(),
        UserId = r.GetString(r.GetOrdinal("user_id")),
        Status = r.GetString(r.GetOrdinal("status")),
        Format = r.GetString(r.GetOrdinal("format")),
        RequestedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("requested_at")),
        DueBy = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("due_by")),
        StartedAt = r.IsDBNull(r.GetOrdinal("started_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("started_at")),
        ReadyAt = r.IsDBNull(r.GetOrdinal("completed_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("completed_at")),
        DeliveredAt = r.IsDBNull(r.GetOrdinal("delivered_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("delivered_at")),
        FailedAt = r.IsDBNull(r.GetOrdinal("failed_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("failed_at")),
        FailureReason = r.IsDBNull(r.GetOrdinal("failure_reason"))
            ? null
            : r.GetString(r.GetOrdinal("failure_reason")),
        DownloadToken = r.IsDBNull(r.GetOrdinal("download_token"))
            ? null
            : r.GetString(r.GetOrdinal("download_token")),
        LinkExpiresAt = r.IsDBNull(r.GetOrdinal("expires_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("expires_at")),
        Payload = r.IsDBNull(r.GetOrdinal("payload"))
            ? null
            : r.GetFieldValue<byte[]>(r.GetOrdinal("payload")),
        PayloadContentType = r.IsDBNull(r.GetOrdinal("payload_content_type"))
            ? null
            : r.GetString(r.GetOrdinal("payload_content_type")),
        PayloadSizeBytes = r.IsDBNull(r.GetOrdinal("payload_size_bytes"))
            ? null
            : r.GetInt64(r.GetOrdinal("payload_size_bytes")),
    };
}
