using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Push;

/// <summary>
/// Postgres-backed <see cref="IPushDeliveryTracker"/> (JEBV4-136, AUDIT-A
/// IN-MEM-LIVE durability follow-up).
///
/// <para>Replaces <see cref="InMemoryPushDeliveryTracker"/> in production. The
/// tracker is the append-only log of every push delivery outcome that lets the ops
/// dashboard / SLO analytics answer "did this user receive the push?" and "what is
/// the retry-path save rate?". In-memory the whole history evaporated on restart.
/// This store persists it to the <c>push_delivery_tracker</c> table (migration 0030).</para>
///
/// <para>Semantics are preserved:
/// <list type="bullet">
/// <item><see cref="RecordAsync"/> — append-only insert; rows are never updated.
/// <see cref="PushDeliveryResult.Trigger"/> and <see cref="PushDeliveryResult.Outcome"/>
/// are stored as their enum member names (lossless, ordinal-drift-proof).</item>
/// <item><see cref="GetForUserAsync"/> — every outcome recorded for a user.</item>
/// <item><see cref="GetRecentAsync"/> — the most recent <c>limit</c> outcomes (newest
/// first), the natural read for a live ops dashboard.</item>
/// </list></para>
/// </summary>
public sealed class PostgresPushDeliveryTracker : IPushDeliveryTracker
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresPushDeliveryTracker> _log;

    public PostgresPushDeliveryTracker(INpgsqlConnectionFactory db, ILogger<PostgresPushDeliveryTracker> log)
    {
        _db = db;
        _log = log;
    }

    public async Task RecordAsync(PushDeliveryResult result, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            INSERT INTO push_delivery_tracker (user_id, trigger, outcome, attempts_made, reason)
            VALUES (@UserId, @Trigger, @Outcome, @AttemptsMade, @Reason)
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", result.UserId);
        cmd.Parameters.AddWithValue("Trigger", result.Trigger.ToString());
        cmd.Parameters.AddWithValue("Outcome", result.Outcome.ToString());
        cmd.Parameters.AddWithValue("AttemptsMade", result.AttemptsMade);
        cmd.Parameters.AddWithValue("Reason", (object?)result.Reason ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<PushDeliveryResult>> GetForUserAsync(string userId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT user_id, trigger, outcome, attempts_made, reason
            FROM push_delivery_tracker
            WHERE user_id = @UserId
            ORDER BY id DESC
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        return await ReadListAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<PushDeliveryResult>> GetRecentAsync(int limit, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            SELECT user_id, trigger, outcome, attempts_made, reason
            FROM push_delivery_tracker
            ORDER BY id DESC
            LIMIT @Limit
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Limit", Math.Max(0, limit));
        return await ReadListAsync(cmd, ct);
    }

    private static async Task<List<PushDeliveryResult>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<PushDeliveryResult>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var reasonOrdinal = reader.GetOrdinal("reason");
            results.Add(new PushDeliveryResult(
                UserId: reader.GetString(reader.GetOrdinal("user_id")),
                Trigger: Enum.Parse<NotificationTrigger>(reader.GetString(reader.GetOrdinal("trigger"))),
                Outcome: Enum.Parse<PushDeliveryOutcome>(reader.GetString(reader.GetOrdinal("outcome"))),
                AttemptsMade: reader.GetInt32(reader.GetOrdinal("attempts_made")),
                Reason: reader.IsDBNull(reasonOrdinal) ? null : reader.GetString(reasonOrdinal)));
        }
        return results;
    }
}
