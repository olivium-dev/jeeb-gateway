using System.Text.Json;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Push;

/// <summary>
/// Postgres-backed <see cref="IPushRetryQueue"/> (JEBV4-137, AUDIT-A IN-MEM-LIVE
/// durability follow-up).
///
/// <para>Replaces <see cref="InMemoryPushRetryQueue"/> in production. This queue
/// holds notifications that failed their first send and are due for the single 30s
/// retry (T-backend-022 AC). In-memory it evaporated on restart — every entry
/// awaiting its one retry was silently dropped. This store persists it to the
/// <c>push_retry_queue</c> table (migration 0030).</para>
///
/// <para>Semantics are preserved exactly:
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> — a plain append; the full
/// <see cref="PushNotificationRequest"/> is serialized to a JSONB blob so the retry
/// replays the identical request (trigger, title/body, data, idempotency key, locale).</item>
/// <item><see cref="DrainDueAsync"/> — atomically <c>DELETE … RETURNING</c> every row
/// whose <c>due_at</c> is in the past, so entries are drained-and-removed and
/// <b>never re-enqueued</b> (the "retried once, then a hard failure" policy). The
/// atomic delete is inherently concurrency-safe: each row is handed to exactly one
/// sweeper across all replicas — no <c>SKIP LOCKED</c> juggling and no double retry.</item>
/// </list></para>
/// </summary>
public sealed class PostgresPushRetryQueue : IPushRetryQueue
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresPushRetryQueue> _log;

    public PostgresPushRetryQueue(INpgsqlConnectionFactory db, ILogger<PostgresPushRetryQueue> log)
    {
        _db = db;
        _log = log;
    }

    public async Task EnqueueAsync(PushRetryEntry entry, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO push_retry_queue (request, due_at, failure_reason)
            VALUES (@Request, @DueAt, @FailureReason)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("Request", NpgsqlDbType.Jsonb)
        {
            Value = JsonSerializer.Serialize(entry.Request)
        });
        cmd.Parameters.AddWithValue("DueAt", entry.DueAt);
        cmd.Parameters.AddWithValue("FailureReason", entry.FailureReason ?? string.Empty);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PushRetryEntry>> DrainDueAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // Atomic drain: every due row is deleted and returned in a single statement,
        // so a row is handed to exactly one sweeper (concurrency-safe, never re-enqueued).
        const string sql = """
            DELETE FROM push_retry_queue
             WHERE due_at <= @Now
            RETURNING request, due_at, failure_reason
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Now", now);

        var results = new List<PushRetryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var requestJson = reader.GetString(0);
            var request = JsonSerializer.Deserialize<PushNotificationRequest>(requestJson);
            if (request is null)
            {
                _log.LogWarning("push_retry_queue row had an unparseable request payload; skipping");
                continue;
            }
            results.Add(new PushRetryEntry(
                Request: request,
                DueAt: reader.GetFieldValue<DateTimeOffset>(1),
                FailureReason: reader.GetString(2)));
        }
        return results;
    }

    /// <summary>
    /// Diagnostics-only count of queued entries. The interface exposes this
    /// synchronously; a short blocking scalar query is acceptable for a rarely-hit probe.
    /// </summary>
    public int PendingCount
    {
        get
        {
            using var conn = _db.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
            using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM push_retry_queue", conn);
            var scalar = cmd.ExecuteScalar();
            return scalar is long l ? (int)l : 0;
        }
    }
}
