using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Requests;

/// <summary>
/// Postgres-backed <see cref="IDurableRequestsMirror"/> (requests-durable).
///
/// Mirrors the raw-Npgsql shape of
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/>: an
/// <see cref="INpgsqlConnectionFactory"/> per operation, parameterised
/// <see cref="NpgsqlCommand"/>s (no string interpolation of values), and
/// <c>INSERT … ON CONFLICT (id) DO NOTHING</c> for insert-once idempotency.
///
/// <para>The create mirror writes the native <c>status</c> as the constant
/// <c>'pending'</c> — which satisfies delivery_requests' status↔timestamp CHECK
/// constraints (cancelled/delivered/tier-required/scheduled consistency)
/// unconditionally — and carries the REAL gateway status in <c>gw_status</c>
/// (migration 0024). Geography points are written with
/// <c>ST_SetSRID(ST_MakePoint(lng,lat),4326)</c> and read back with
/// <c>ST_X</c>/<c>ST_Y</c>, so no NetTopologySuite mapping is needed.</para>
///
/// <para>Registered ONLY inside the <c>GatewayPostgres:ConnectionString</c> block
/// (alongside <see cref="JeebGateway.Financials.PostgresSettlementStore"/>) since
/// it depends on the connection factory registered there; the decorator resolves
/// it as an OPTIONAL dependency and degrades to the in-memory owner-list when it
/// is absent.</para>
/// </summary>
public sealed class PostgresDurableRequestsMirror : IDurableRequestsMirror
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresDurableRequestsMirror> _log;

    public PostgresDurableRequestsMirror(INpgsqlConnectionFactory db, ILogger<PostgresDurableRequestsMirror> log)
    {
        _db = db;
        _log = log;
    }

    public async Task UpsertOnCreateAsync(DeliveryRequest row, CancellationToken ct)
    {
        // The native id / client_id are UUID columns (client_id also FK → users);
        // a non-UUID id simply never enters the durable mirror (the owner-list
        // then degrades to the in-memory snapshot for that row). Geography is
        // NOT NULL for both points, so a row missing either is not mirrorable.
        if (!Guid.TryParse(row.Id, out var id)) return;
        if (!Guid.TryParse(row.ClientId, out var clientId)) return;
        if (row.PickupLocation is null || row.DropoffLocation is null) return;

        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO delivery_requests (
                id, client_id, description, audio_url, transcription,
                pickup_location, dropoff_location, pickup_address, dropoff_address,
                status, scheduled_at, created_at,
                gw_mirror, gw_status, gw_jeeber_id, gw_tier_code,
                gw_conversation_id, gw_accepted_fee, gw_recipient_phone, gw_updated_at
            ) VALUES (
                @Id, @ClientId, @Description, @AudioUrl, @Transcription,
                ST_SetSRID(ST_MakePoint(@PickupLng, @PickupLat), 4326)::geography,
                ST_SetSRID(ST_MakePoint(@DropoffLng, @DropoffLat), 4326)::geography,
                @PickupAddress, @DropoffAddress,
                'pending'::delivery_request_status, @ScheduledAt, @CreatedAt,
                TRUE, @GwStatus, @GwJeeberId, @GwTierCode,
                @GwConversationId, @GwAcceptedFee, @GwRecipientPhone, now()
            )
            ON CONFLICT (id) DO NOTHING
            """;

        await using var cmd = new NpgsqlCommand(insertSql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("ClientId", clientId);
        cmd.Parameters.AddWithValue("Description", row.Description);
        cmd.Parameters.AddWithValue("AudioUrl", (object?)row.AudioUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("Transcription", (object?)row.Transcription ?? DBNull.Value);
        cmd.Parameters.AddWithValue("PickupLat", row.PickupLocation.Lat);
        cmd.Parameters.AddWithValue("PickupLng", row.PickupLocation.Lng);
        cmd.Parameters.AddWithValue("DropoffLat", row.DropoffLocation.Lat);
        cmd.Parameters.AddWithValue("DropoffLng", row.DropoffLocation.Lng);
        cmd.Parameters.AddWithValue("PickupAddress", (object?)row.PickupAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("DropoffAddress", (object?)row.DropoffAddress ?? DBNull.Value);
        cmd.Parameters.AddWithValue("ScheduledAt", (object?)row.ScheduledAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("CreatedAt", row.CreatedAt);
        cmd.Parameters.AddWithValue("GwStatus", row.Status);
        cmd.Parameters.AddWithValue("GwJeeberId", (object?)row.JeeberId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwTierCode", (object?)row.TierId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwConversationId", (object?)row.ConversationId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwAcceptedFee", (object?)row.AcceptedFee ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwRecipientPhone", (object?)row.RecipientPhone ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogDebug(
            "requests-durable: mirrored request {RequestId} for client {ClientId} into delivery_requests (gw_status={GwStatus}).",
            row.Id, row.ClientId, row.Status);
    }

    public async Task MarkCancelledAsync(
        string requestId,
        string gwStatus,
        string? cancelledBy,
        string? cancellationReason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id)) return;

        await using var conn = await _db.OpenAsync(ct);

        // Touch ONLY the gateway columns — the native enum status + its coupled
        // CHECK constraints are left exactly as the create mirror wrote them
        // ('pending'), so no constraint can fire on a cancel of any shape.
        const string sql = """
            UPDATE delivery_requests
               SET gw_status              = @GwStatus,
                   gw_cancelled_by        = @CancelledBy,
                   gw_cancellation_reason = @CancellationReason,
                   gw_cancelled_at        = @At,
                   gw_updated_at          = now()
             WHERE id = @Id AND gw_mirror = TRUE
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("GwStatus", gwStatus);
        cmd.Parameters.AddWithValue("CancelledBy", (object?)cancelledBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("CancellationReason", (object?)cancellationReason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("At", at);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkExpiredAsync(
        string requestId,
        DateTimeOffset expiredAt,
        CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id)) return;

        await using var conn = await _db.OpenAsync(ct);

        // Touch ONLY the gateway columns — the native enum status + its coupled
        // CHECK constraints are left untouched so no constraint can fire.
        const string sql = """
            UPDATE delivery_requests
               SET gw_status = 'expired',
                   gw_expired_at = @At,
                   gw_updated_at = now()
             WHERE id = @Id AND gw_mirror = TRUE
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("At", expiredAt);

        await cmd.ExecuteNonQueryAsync(ct);

        _log.LogDebug(
            "requests-durable: mirrored request {RequestId} expiry into delivery_requests (gw_status=expired).",
            requestId);
    }

    public async Task UpdateLifecycleAsync(
        string requestId,
        string? gwStatus,
        string? gwJeeberId,
        decimal? gwAcceptedFee,
        DateTimeOffset at,
        CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id)) return;

        // Nothing to reflect — avoid a needless round-trip.
        if (gwStatus is null && gwJeeberId is null && gwAcceptedFee is null) return;

        await using var conn = await _db.OpenAsync(ct);

        // Touch ONLY the gateway columns. COALESCE(@X, col) leaves a column as-is when
        // its argument is NULL, so a status-only / jeeber-only / fee-only mutation
        // updates just what changed. The native enum status + its coupled CHECK
        // constraints are never touched, so no constraint can fire on any mutation.
        const string sql = """
            UPDATE delivery_requests
               SET gw_status       = COALESCE(@GwStatus, gw_status),
                   gw_jeeber_id    = COALESCE(@GwJeeberId, gw_jeeber_id),
                   gw_accepted_fee = COALESCE(@GwAcceptedFee, gw_accepted_fee),
                   gw_updated_at   = now()
             WHERE id = @Id AND gw_mirror = TRUE
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("GwStatus", (object?)gwStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwJeeberId", (object?)gwJeeberId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("GwAcceptedFee", (object?)gwAcceptedFee ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct)
    {
        // client_id is a UUID column; a non-UUID client can have no mirror rows.
        if (!Guid.TryParse(clientId, out var clientGuid)) return Array.Empty<DeliveryRequest>();

        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT
                id,
                client_id,
                COALESCE(gw_status, status::text)  AS status,
                description,
                transcription,
                audio_url,
                gw_tier_code,
                ST_Y(pickup_location::geometry)     AS pickup_lat,
                ST_X(pickup_location::geometry)     AS pickup_lng,
                ST_Y(dropoff_location::geometry)    AS dropoff_lat,
                ST_X(dropoff_location::geometry)    AS dropoff_lng,
                pickup_address,
                dropoff_address,
                gw_recipient_phone,
                created_at,
                scheduled_at,
                gw_jeeber_id,
                gw_accepted_fee,
                gw_conversation_id,
                gw_cancelled_by,
                gw_cancellation_reason,
                gw_cancelled_at
            FROM delivery_requests
            WHERE client_id = @ClientId AND gw_mirror = TRUE
            ORDER BY created_at ASC
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("ClientId", clientGuid);
        return await ReadListAsync(cmd, ct);
    }

    /// <summary>
    /// JEBV4-140: durable jeeber-side owner-list. Symmetric with
    /// <see cref="ListForClientAsync"/> — reads the mirror rows the jeeber has been
    /// assigned (<c>gw_jeeber_id</c>) so a jeeber's accepted deliveries survive a
    /// process bounce. Newest-first, matching the in-memory
    /// <see cref="InMemoryRequestsStore.ListForJeeberAsync"/> ordering. The
    /// <c>gw_jeeber_id</c> column is a text column (seeded verbatim from the gateway
    /// jeeber id, not a UUID FK like <c>client_id</c>), so it is compared as text and
    /// a non-UUID jeeber id is valid.
    /// </summary>
    public async Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(jeeberId)) return Array.Empty<DeliveryRequest>();

        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT
                id,
                client_id,
                COALESCE(gw_status, status::text)  AS status,
                description,
                transcription,
                audio_url,
                gw_tier_code,
                ST_Y(pickup_location::geometry)     AS pickup_lat,
                ST_X(pickup_location::geometry)     AS pickup_lng,
                ST_Y(dropoff_location::geometry)    AS dropoff_lat,
                ST_X(dropoff_location::geometry)    AS dropoff_lng,
                pickup_address,
                dropoff_address,
                gw_recipient_phone,
                created_at,
                scheduled_at,
                gw_jeeber_id,
                gw_accepted_fee,
                gw_conversation_id,
                gw_cancelled_by,
                gw_cancellation_reason,
                gw_cancelled_at
            FROM delivery_requests
            WHERE gw_jeeber_id = @JeeberId AND gw_mirror = TRUE
            ORDER BY created_at DESC
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("JeeberId", jeeberId);
        return await ReadListAsync(cmd, ct);
    }

    /// <summary>
    /// JEBV4-248: durable by-id read. Same column projection + <see cref="MapRow"/>
    /// as the owner-list queries, filtered to the single mirror row. The native id
    /// is a UUID column, so a non-UUID id has no mirror row (returns null). Used as
    /// the by-id backstop so a row visible in the owner-list is also resolvable by id.
    /// </summary>
    public async Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out var id)) return null;

        await using var conn = await _db.OpenAsync(ct);

        const string sql = """
            SELECT
                id,
                client_id,
                COALESCE(gw_status, status::text)  AS status,
                description,
                transcription,
                audio_url,
                gw_tier_code,
                ST_Y(pickup_location::geometry)     AS pickup_lat,
                ST_X(pickup_location::geometry)     AS pickup_lng,
                ST_Y(dropoff_location::geometry)    AS dropoff_lat,
                ST_X(dropoff_location::geometry)    AS dropoff_lng,
                pickup_address,
                dropoff_address,
                gw_recipient_phone,
                created_at,
                scheduled_at,
                gw_jeeber_id,
                gw_accepted_fee,
                gw_conversation_id,
                gw_cancelled_by,
                gw_cancellation_reason,
                gw_cancelled_at
            FROM delivery_requests
            WHERE id = @Id AND gw_mirror = TRUE
            LIMIT 1
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", id);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<List<DeliveryRequest>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<DeliveryRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static DeliveryRequest MapRow(NpgsqlDataReader r)
    {
        string? Text(string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
        DateTimeOffset? Ts(string col) => r.IsDBNull(r.GetOrdinal(col))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal(col));

        return new DeliveryRequest
        {
            Id          = r.GetGuid(r.GetOrdinal("id")).ToString(),
            ClientId    = r.GetGuid(r.GetOrdinal("client_id")).ToString(),
            Status      = r.GetString(r.GetOrdinal("status")),
            Description = r.GetString(r.GetOrdinal("description")),
            Transcription = Text("transcription"),
            AudioUrl    = Text("audio_url"),
            TierId      = Text("gw_tier_code"),
            PickupLocation = new GeoPoint
            {
                Lat = r.GetDouble(r.GetOrdinal("pickup_lat")),
                Lng = r.GetDouble(r.GetOrdinal("pickup_lng")),
            },
            DropoffLocation = new GeoPoint
            {
                Lat = r.GetDouble(r.GetOrdinal("dropoff_lat")),
                Lng = r.GetDouble(r.GetOrdinal("dropoff_lng")),
            },
            PickupAddress  = Text("pickup_address"),
            DropoffAddress = Text("dropoff_address"),
            RecipientPhone = Text("gw_recipient_phone"),
            CreatedAt      = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("created_at")),
            ScheduledAt    = Ts("scheduled_at"),
            JeeberId       = Text("gw_jeeber_id"),
            AcceptedFee    = r.IsDBNull(r.GetOrdinal("gw_accepted_fee"))
                ? null
                : r.GetDecimal(r.GetOrdinal("gw_accepted_fee")),
            ConversationId = Text("gw_conversation_id"),
            CancelledBy    = Text("gw_cancelled_by"),
            CancellationReason = Text("gw_cancellation_reason"),
            CancellationRequestedAt = Ts("gw_cancelled_at"),
        };
    }
}
