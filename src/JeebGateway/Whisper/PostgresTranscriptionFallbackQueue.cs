using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Whisper;

/// <summary>
/// Postgres-backed <see cref="ITranscriptionFallbackQueue"/> (JEBV4-126, AUDIT-A
/// IN-MEM-LIVE durability follow-up).
///
/// <para>Replaces <see cref="InMemoryTranscriptionFallbackQueue"/> in production. This
/// queue holds the small metadata rows for voice notes whose transcription fell back
/// (Whisper exhausted its retries / circuit open AND the secondary provider was
/// unavailable) and that must be re-driven once Whisper recovers — an
/// <see cref="QueuedTranscription"/> is just <c>(AudioId, Reason, QueuedAt)</c>, no
/// audio bytes. In-memory this evaporated on every restart / replica move, so the
/// pending-retry backlog and the <c>PendingQueueDepth</c> that drives the Whisper
/// health check and the transcription status endpoint silently reset to zero. This
/// store persists it to the <c>transcription_fallback_queue</c> table (migration
/// 0033).</para>
///
/// <para>This is gateway-OWNED reliability plumbing (the gateway is the transcription
/// composer for the MVP Whisper seam; there is no upstream queue service that owns it
/// yet), so its durable home is gateway Postgres, alongside the push-reliability
/// queues (migration 0031) and the other AUDIT-A durability tables. NOTE: only the
/// job metadata lives here — the raw audio bytes deliberately do NOT (large blobs do
/// not belong in the gateway DB; see <see cref="IAudioStore"/>).</para>
///
/// <para>Semantics are preserved exactly:
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> — a plain append; the row records the audio id,
/// the fallback reason and the enqueue timestamp.</item>
/// <item><see cref="Snapshot"/> — reads back every queued row (insertion order), the
/// durable form of the in-memory <c>ConcurrentQueue.ToArray()</c>. It backs
/// diagnostics only (queue depth on the health check + status endpoint), so a short
/// blocking read on a rarely-hit probe is acceptable, matching
/// <c>PostgresPushRetryQueue.PendingCount</c>.</item>
/// </list></para>
/// </summary>
public sealed class PostgresTranscriptionFallbackQueue : ITranscriptionFallbackQueue
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresTranscriptionFallbackQueue> _log;

    public PostgresTranscriptionFallbackQueue(
        INpgsqlConnectionFactory db,
        ILogger<PostgresTranscriptionFallbackQueue> log)
    {
        _db = db;
        _log = log;
    }

    public async Task EnqueueAsync(QueuedTranscription item, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO transcription_fallback_queue (audio_id, reason, queued_at)
            VALUES (@AudioId, @Reason, @QueuedAt)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("AudioId", item.AudioId);
        cmd.Parameters.AddWithValue("Reason", item.Reason ?? string.Empty);
        cmd.Parameters.AddWithValue("QueuedAt", item.QueuedAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Diagnostics read of every queued entry (insertion order). The interface exposes
    /// this synchronously; a short blocking query on a rarely-hit health/status probe is
    /// acceptable, mirroring <c>PostgresPushRetryQueue.PendingCount</c>.
    /// </summary>
    public IReadOnlyCollection<QueuedTranscription> Snapshot()
    {
        using var conn = _db.OpenAsync(CancellationToken.None).GetAwaiter().GetResult();
        const string sql = """
            SELECT audio_id, reason, queued_at
              FROM transcription_fallback_queue
             ORDER BY id
            """;
        using var cmd = new NpgsqlCommand(sql, conn);

        var results = new List<QueuedTranscription>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new QueuedTranscription(
                AudioId: reader.GetString(0),
                Reason: reader.GetString(1),
                QueuedAt: reader.GetFieldValue<DateTimeOffset>(2)));
        }
        return results;
    }
}
