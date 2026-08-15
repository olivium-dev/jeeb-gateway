using System.Text.Json;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Postgres-backed <see cref="INotificationDispatchOutbox"/> (JEBV4-144, AUDIT-A
/// IN-MEM-LIVE durability follow-up).
///
/// <para>Replaces <see cref="InMemoryNotificationDispatchOutbox"/> in production.
/// The outbox is the gateway's render→dispatch system of record (JEB-1494): it
/// de-dups on the idempotency key, schedules the single 30s retry, and moves an
/// entry to the DLQ after max attempts. In-memory it evaporated on every restart —
/// so a queued-but-not-yet-delivered push was silently dropped. This store persists
/// it to the <c>notification_dispatch_outbox</c> table (migration 0030).</para>
///
/// <para>Semantics are preserved exactly vs the in-memory store:
/// <list type="bullet">
/// <item><see cref="ExistsAsync"/> — an Ordinal idempotency-key match, now enforced
/// durably by the partial-unique index <c>uq_notif_outbox_idempotency_key</c>.</item>
/// <item><see cref="AddAsync"/> — inserts the row exactly as constructed (the entry
/// already carries its Id/CreatedAt defaults), so the returned object matches the
/// persisted row byte-for-byte.</item>
/// <item><see cref="MarkDeliveredAsync"/> / <see cref="RecordFailureAsync"/> — mutate
/// by primary key; RecordFailure increments the attempt count and either moves to
/// <c>DLQ</c> (attempts &gt;= max) or schedules <c>next_attempt_at = now + retryDelay</c>,
/// identical to the in-memory branch.</item>
/// <item><see cref="GetDlqAsync"/> — reads every DLQ row for the observability endpoint.</item>
/// </list></para>
///
/// <para><b>Concurrency-safe dequeue.</b> <see cref="GetDueAsync"/> is a claiming
/// read: it atomically selects the due <c>Pending</c> rows with
/// <c>FOR UPDATE SKIP LOCKED</c> (oldest first — FIFO) and advances their
/// <c>next_attempt_at</c> by a short visibility lease before returning them, so two
/// gateway replicas sweeping concurrently never hand the same entry to the transport
/// twice. If the claimer crashes before marking the row delivered/failed, the lease
/// expires and the row becomes due again — at-least-once, never dropped.</para>
/// </summary>
public sealed class PostgresNotificationDispatchOutbox : INotificationDispatchOutbox
{
    /// <summary>How long a GetDue claim hides a row from other sweepers before it
    /// becomes due again (crash-safety window). Generous relative to the 30s retry
    /// cadence so a normal send/mark cycle always completes inside it.</summary>
    private static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(2);

    private const string SelectColumns =
        "id, template_key, locale, parameters, recipient_user_id, idempotency_key, " +
        "status, attempt_count, created_at, next_attempt_at, last_error";

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresNotificationDispatchOutbox> _log;

    public PostgresNotificationDispatchOutbox(INpgsqlConnectionFactory db, ILogger<PostgresNotificationDispatchOutbox> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT 1 FROM notification_dispatch_outbox WHERE idempotency_key = @Key LIMIT 1", conn);
        cmd.Parameters.AddWithValue("Key", idempotencyKey);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<NotificationDispatchEntry> AddAsync(NotificationDispatchEntry entry, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO notification_dispatch_outbox
                (id, template_key, locale, parameters, recipient_user_id, idempotency_key,
                 status, attempt_count, created_at, next_attempt_at, last_error)
            VALUES
                (@Id, @TemplateKey, @Locale, @Parameters, @RecipientUserId, @IdempotencyKey,
                 @Status, @AttemptCount, @CreatedAt, @NextAttemptAt, @LastError)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", entry.Id);
        cmd.Parameters.AddWithValue("TemplateKey", entry.TemplateKey);
        cmd.Parameters.AddWithValue("Locale", entry.Locale);
        cmd.Parameters.Add(new NpgsqlParameter("Parameters", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(entry.Parameters)
        });
        cmd.Parameters.AddWithValue("RecipientUserId", entry.RecipientUserId);
        cmd.Parameters.AddWithValue("IdempotencyKey", (object?)entry.IdempotencyKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Status", entry.Status.ToString());
        cmd.Parameters.AddWithValue("AttemptCount", entry.AttemptCount);
        cmd.Parameters.AddWithValue("CreatedAt", entry.CreatedAt);
        cmd.Parameters.AddWithValue("NextAttemptAt", (object?)entry.NextAttemptAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("LastError", (object?)entry.LastError ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
        return entry;
    }

    public async Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Atomic claim: lock the due Pending rows (skipping any a peer already holds),
        // push their visibility out by the lease, and return them. FIFO by created_at.
        const string sql = """
            UPDATE notification_dispatch_outbox
               SET next_attempt_at = @LeaseUntil
             WHERE id IN (
                   SELECT id FROM notification_dispatch_outbox
                    WHERE status = 'Pending'
                      AND (next_attempt_at IS NULL OR next_attempt_at <= @Now)
                    ORDER BY created_at ASC
                    FOR UPDATE SKIP LOCKED
             )
            RETURNING id, template_key, locale, parameters, recipient_user_id, idempotency_key,
                      status, attempt_count, created_at, next_attempt_at, last_error
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Now", now);
        cmd.Parameters.AddWithValue("LeaseUntil", now.Add(ClaimLease));
        return await ReadListAsync(cmd, ct);
    }

    public async Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "UPDATE notification_dispatch_outbox SET status = 'Delivered' WHERE id = @Id", conn);
        cmd.Parameters.AddWithValue("Id", entryId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RecordFailureAsync(Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Single round-trip: increment the attempt, then DLQ once attempts >= max,
        // else schedule the next retry — the exact branch InMemory takes, computed
        // in-SQL against the post-increment count so it is race-free per row.
        const string sql = """
            UPDATE notification_dispatch_outbox
               SET attempt_count   = attempt_count + 1,
                   last_error      = @Error,
                   status          = CASE WHEN attempt_count + 1 >= @MaxAttempts THEN 'DLQ' ELSE status END,
                   next_attempt_at = CASE WHEN attempt_count + 1 >= @MaxAttempts THEN next_attempt_at ELSE @NextAttemptAt END
             WHERE id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", entryId);
        cmd.Parameters.AddWithValue("Error", error);
        cmd.Parameters.AddWithValue("MaxAttempts", maxAttempts);
        cmd.Parameters.AddWithValue("NextAttemptAt", DateTimeOffset.UtcNow.Add(retryDelay));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {SelectColumns} FROM notification_dispatch_outbox WHERE status = 'DLQ' ORDER BY created_at ASC", conn);
        return await ReadListAsync(cmd, ct);
    }

    /// <summary>
    /// Diagnostics-only pending count. The interface exposes this synchronously; a
    /// short blocking scalar query is acceptable for a rarely-hit diagnostics probe.
    /// </summary>
    public int PendingCount
    {
        get
        {
            using var conn = _db.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var cmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM notification_dispatch_outbox WHERE status = 'Pending'", conn);
            var scalar = cmd.ExecuteScalar();
            return scalar is long l ? (int)l : 0;
        }
    }

    private static async Task<List<NotificationDispatchEntry>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<NotificationDispatchEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static NotificationDispatchEntry MapRow(NpgsqlDataReader r)
    {
        var paramsJson = r.GetString(r.GetOrdinal("parameters"));
        var parameters = JsonSerializer.Deserialize<Dictionary<string, string>>(paramsJson)
                         ?? new Dictionary<string, string>();
        var idemOrdinal = r.GetOrdinal("idempotency_key");
        var nextOrdinal = r.GetOrdinal("next_attempt_at");
        var errOrdinal = r.GetOrdinal("last_error");

        return new NotificationDispatchEntry
        {
            Id              = r.GetGuid(r.GetOrdinal("id")),
            TemplateKey     = r.GetString(r.GetOrdinal("template_key")),
            Locale          = r.GetString(r.GetOrdinal("locale")),
            Parameters      = parameters,
            RecipientUserId = r.GetGuid(r.GetOrdinal("recipient_user_id")),
            IdempotencyKey  = r.IsDBNull(idemOrdinal) ? null : r.GetString(idemOrdinal),
            Status          = Enum.Parse<NotificationDispatchStatus>(r.GetString(r.GetOrdinal("status"))),
            AttemptCount    = r.GetInt32(r.GetOrdinal("attempt_count")),
            CreatedAt       = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
            NextAttemptAt   = r.IsDBNull(nextOrdinal) ? null : r.GetFieldValue<DateTimeOffset>(nextOrdinal),
            LastError       = r.IsDBNull(errOrdinal) ? null : r.GetString(errOrdinal),
        };
    }
}
