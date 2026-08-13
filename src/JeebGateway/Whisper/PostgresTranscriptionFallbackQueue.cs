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
/// store persisted it to the <c>transcription_fallback_queue</c> table (migration
/// 0033).</para>
///
/// <para>W3-06 (A12): the WRITE to that table is deleted — no new row can be created.
/// <see cref="Snapshot"/> still READS it so the pre-existing backlog stays visible on
/// the health check / status probe until the owner-gated table DROP (W5-09), which must
/// remove this read in the same change.</para>
///
/// <para>This is gateway-OWNED reliability plumbing (the gateway is the transcription
/// composer for the MVP Whisper seam; there is no upstream queue service that owns it
/// yet), so its durable home is gateway Postgres, alongside the push-reliability
/// queues (migration 0031) and the other AUDIT-A durability tables. NOTE: only the
/// job metadata lives here — the raw audio bytes deliberately do NOT (large blobs do
/// not belong in the gateway DB; see <see cref="IAudioStore"/>).</para>
///
/// <para>Semantics:
/// <list type="bullet">
/// <item><see cref="EnqueueAsync"/> — W3-06/A12: the durable append is DELETED; the
/// fallback event is logged instead (see the method).</item>
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

    /// <summary>
    /// W3-06 (A12): the enqueue WRITE is deleted — a queued row could re-drive nothing
    /// (the audio bytes live only in the in-memory <see cref="IAudioStore"/>). The
    /// fallback stays observable as a structured log event instead. No I/O, never throws.
    /// </summary>
    public Task EnqueueAsync(QueuedTranscription item, CancellationToken ct)
    {
        _log.LogWarning(
            "transcription fallback NOT queued (W3-06/A12: enqueue write deleted): AudioId={AudioId} Reason={Reason} QueuedAt={QueuedAt}",
            item.AudioId, item.Reason ?? string.Empty, item.QueuedAt);
        return Task.CompletedTask;
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
