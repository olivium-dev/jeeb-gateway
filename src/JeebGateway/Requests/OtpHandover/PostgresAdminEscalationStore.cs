using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>
/// Postgres-backed <see cref="IAdminEscalationStore"/> (T-backend-015 / JEEB-33,
/// gateway durability hardening).
///
/// Replaces <see cref="InMemoryAdminEscalationStore"/> in production so admin
/// escalation rows opened by the OTP handover flow (lockout / client-unreachable —
/// see <see cref="EscalationReason"/>) survive a gateway bounce and are visible
/// across replicas. Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s
/// raw-Npgsql shape (same connection factory, param binding, and
/// ReadListAsync/MapRow reader helpers).
///
/// <para><b>No per-delivery uniqueness invariant.</b> Unlike <c>settlements</c>
/// (<c>UNIQUE(delivery_id)</c> + <c>ON CONFLICT DO NOTHING</c>), the
/// <c>admin_escalations</c> table carries no uniqueness constraint — per
/// <see cref="IAdminEscalationStore.CreateAsync"/>'s contract, the store does NOT
/// enforce a per-delivery uniqueness invariant. Callers
/// (<see cref="InMemoryRequestsStore.TryVerifyOtpAsync"/> and
/// <c>OtpHandoverSweeper</c>) instead use the write-once
/// <c>DeliveryRequest.OtpEscalationId</c> field to prevent duplicate escalations, so
/// <see cref="CreateAsync"/> here is a plain INSERT with no conflict branch.</para>
/// </summary>
public sealed class PostgresAdminEscalationStore : IAdminEscalationStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresAdminEscalationStore> _log;

    public PostgresAdminEscalationStore(INpgsqlConnectionFactory db, ILogger<PostgresAdminEscalationStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<AdminEscalation> CreateAsync(AdminEscalation entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);

        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO admin_escalations (
                id, delivery_id, client_id, jeeber_id, reason, status,
                otp_attempt_count, created_at, updated_at
            ) VALUES (
                @Id, @DeliveryId, @ClientId, @JeeberId, @Reason, @Status,
                @OtpAttemptCount, @CreatedAt, now()
            )
            RETURNING *
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.Parse(entry.Id));
        insertCmd.Parameters.AddWithValue("DeliveryId", entry.DeliveryId);
        insertCmd.Parameters.AddWithValue("ClientId", entry.ClientId);
        insertCmd.Parameters.AddWithValue("JeeberId", (object?)entry.JeeberId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("Reason", entry.Reason);
        insertCmd.Parameters.AddWithValue("Status", entry.Status);
        insertCmd.Parameters.AddWithValue("OtpAttemptCount", entry.OtpAttemptCount);
        insertCmd.Parameters.AddWithValue("CreatedAt", entry.CreatedAt);

        var rows = await ReadListAsync(insertCmd, ct);
        var row = rows[0];

        _log.LogInformation(
            "Admin escalation opened deliveryId={DeliveryId} escalationId={Id} reason={Reason}",
            row.DeliveryId, row.Id, row.Reason);

        return row;
    }

    public async Task<AdminEscalation?> GetForDeliveryAsync(string deliveryId, string reason, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // Most-recent-first + LIMIT 1: GetForDeliveryAsync has no uniqueness
        // guarantee to lean on (see class remarks), so pick the latest
        // deterministically rather than an arbitrary row.
        const string sql = """
            SELECT * FROM admin_escalations
            WHERE delivery_id = @DeliveryId AND reason = @Reason
            ORDER BY created_at DESC
            LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("DeliveryId", deliveryId);
        cmd.Parameters.AddWithValue("Reason", reason);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<IReadOnlyList<AdminEscalation>> ListAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        // Newest-first — powers the admin triage queue. Escalation volume is low
        // (lockout / client-unreachable edge cases only), so an unindexed sort here is
        // intentional; see migration 0021 remarks. F9: a bounded LIMIT caps the worst
        // case as the table grows over time — the triage queue only ever needs the most
        // recent escalations, and no caller paginates beyond this page today.
        const string sql = """
            SELECT * FROM admin_escalations
            ORDER BY created_at DESC
            LIMIT 500
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await ReadListAsync(cmd, ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<List<AdminEscalation>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<AdminEscalation>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static AdminEscalation MapRow(NpgsqlDataReader r)
    {
        return new AdminEscalation
        {
            Id              = r.GetGuid(r.GetOrdinal("id")).ToString(),
            DeliveryId      = r.GetString(r.GetOrdinal("delivery_id")),
            ClientId        = r.GetString(r.GetOrdinal("client_id")),
            JeeberId        = r.IsDBNull(r.GetOrdinal("jeeber_id"))
                ? null
                : r.GetString(r.GetOrdinal("jeeber_id")),
            Reason          = r.GetString(r.GetOrdinal("reason")),
            Status          = r.GetString(r.GetOrdinal("status")),
            OtpAttemptCount = r.GetInt32(r.GetOrdinal("otp_attempt_count")),
            CreatedAt       = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
        };
    }
}
