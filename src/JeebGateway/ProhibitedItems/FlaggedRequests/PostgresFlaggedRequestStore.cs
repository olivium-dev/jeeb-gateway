using System.Text.Json;
using JeebGateway.Infrastructure;
using JeebGateway.ProhibitedItems.Scanner;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

/// <summary>
/// Postgres-backed <see cref="IFlaggedRequestStore"/> (T-backend-048 follow-up,
/// gateway durability hardening).
///
/// Replaces <see cref="InMemoryFlaggedRequestStore"/> in production so admin
/// moderation queue rows opened by <c>ProhibitedItemsScanController.Scan</c>
/// survive a gateway bounce and are visible across replicas. Mirrors
/// <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s raw-Npgsql
/// shape — same connection factory, param binding, and
/// ReadListAsync/MapRow reader helpers — and the sibling
/// <see cref="JeebGateway.Requests.OtpHandover.PostgresAdminEscalationStore"/>
/// from the same hardening pass (table 0021).
///
/// <para><b>No per-request uniqueness invariant.</b> Like
/// <c>admin_escalations</c>, <see cref="CreateAsync"/> is a plain INSERT with
/// no ON CONFLICT branch — every scanner hit above the review threshold gets
/// its own row, matching <see cref="InMemoryFlaggedRequestStore"/>'s
/// always-insert <c>Guid.NewGuid()</c> semantics.</para>
///
/// <para><b>Column naming.</b> The <c>matches</c> JSONB column stores the
/// full <see cref="ProhibitedItemMatch"/> list as an opaque JSON blob
/// (System.Text.Json, mirroring
/// <see cref="JeebGateway.Disputes.StateServiceDisputeStore"/>'s
/// serialization idiom) so <see cref="GetAsync"/>/<see cref="ListAsync"/> can
/// round-trip the full admin-queue DTO without re-scanning. The
/// <c>reason</c> column holds <see cref="FlaggedRequest.Description"/> — the
/// scanned request text that triggered the flag (i.e. the "reason" an admin
/// reviews) — see migration 0019 remarks for the full column mapping.</para>
///
/// <para><b>Malformed-id guard.</b> <see cref="GetAsync"/> and
/// <see cref="DecideAsync"/> return <see langword="null"/> for a
/// non-GUID id instead of letting <c>Guid.Parse</c> throw, so a bad
/// admin-console route id 404s (matching
/// <see cref="InMemoryFlaggedRequestStore"/>'s dictionary-miss semantics)
/// rather than 500ing.</para>
/// </summary>
public sealed class PostgresFlaggedRequestStore : IFlaggedRequestStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresFlaggedRequestStore> _log;

    public PostgresFlaggedRequestStore(INpgsqlConnectionFactory db, ILogger<PostgresFlaggedRequestStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<FlaggedRequest> CreateAsync(FlaggedRequestCreate input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO flagged_requests (
                id, request_id, user_id, reason, matches, status, flagged_at
            ) VALUES (
                @Id, @RequestId, @UserId, @Reason, @Matches, @Status, @FlaggedAt
            )
            RETURNING *
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.NewGuid());
        insertCmd.Parameters.AddWithValue("RequestId", (object?)input.RequestId ?? DBNull.Value);
        insertCmd.Parameters.AddWithValue("UserId", input.UserId);
        insertCmd.Parameters.AddWithValue("Reason", input.Description);
        insertCmd.Parameters.Add(new NpgsqlParameter("Matches", NpgsqlDbType.Jsonb)
        {
            Value = SerializeMatches(input.Matches)
        });
        insertCmd.Parameters.AddWithValue("Status", ToDb(FlaggedRequestStatus.Pending));
        insertCmd.Parameters.AddWithValue("FlaggedAt", DateTimeOffset.UtcNow);

        var rows = await ReadListAsync(insertCmd, ct);
        var row = rows[0];

        _log.LogInformation(
            "Flagged request recorded id={Id} requestId={RequestId} matchCount={MatchCount}",
            row.Id, row.RequestId, row.Matches.Count);

        return row;
    }

    public async Task<FlaggedRequest?> GetAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM flagged_requests WHERE id = @Id LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", guid);

        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<FlaggedRequestPage> ListAsync(
        FlaggedRequestStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        var statusText = status.HasValue ? ToDb(status.Value) : null;
        var skip = (page - 1) * pageSize;

        // Mirrors PostgresSettlementStore's "(@Param IS NULL OR col = @Param)"
        // optional-filter idiom.
        const string countSql = """
            SELECT COUNT(*) FROM flagged_requests
            WHERE (@Status IS NULL OR status = @Status)
            """;
        await using var countCmd = new NpgsqlCommand(countSql, conn);
        countCmd.Parameters.AddWithValue("Status", (object?)statusText ?? DBNull.Value);
        var totalResult = await countCmd.ExecuteScalarAsync(ct);
        var total = totalResult is null ? 0 : Convert.ToInt32(totalResult);

        const string listSql = """
            SELECT * FROM flagged_requests
            WHERE (@Status IS NULL OR status = @Status)
            ORDER BY flagged_at DESC
            LIMIT @Limit OFFSET @Offset
            """;
        await using var listCmd = new NpgsqlCommand(listSql, conn);
        listCmd.Parameters.AddWithValue("Status", (object?)statusText ?? DBNull.Value);
        listCmd.Parameters.AddWithValue("Limit", pageSize);
        listCmd.Parameters.AddWithValue("Offset", skip);

        var items = await ReadListAsync(listCmd, ct);
        return new FlaggedRequestPage { Items = items, Total = total };
    }

    public async Task<FlaggedRequest?> DecideAsync(
        string id,
        FlaggedRequestStatus status,
        string adminUserId,
        string? note,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE flagged_requests
            SET status = @Status, reviewer_id = @ReviewerId, reviewed_at = @ReviewedAt, decision_note = @Note
            WHERE id = @Id
            RETURNING *
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", guid);
        cmd.Parameters.AddWithValue("Status", ToDb(status));
        cmd.Parameters.AddWithValue("ReviewerId", adminUserId);
        cmd.Parameters.AddWithValue("ReviewedAt", DateTimeOffset.UtcNow);
        cmd.Parameters.AddWithValue("Note", (object?)note ?? DBNull.Value);

        var rows = await ReadListAsync(cmd, ct);
        if (rows.Count > 0)
        {
            _log.LogInformation(
                "Flagged request decided id={Id} status={Status} reviewerId={ReviewerId}",
                rows[0].Id, rows[0].Status, adminUserId);
        }
        return rows.Count > 0 ? rows[0] : null;
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<List<FlaggedRequest>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<FlaggedRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static FlaggedRequest MapRow(NpgsqlDataReader r)
    {
        return new FlaggedRequest
        {
            Id = r.GetGuid(r.GetOrdinal("id")).ToString(),
            RequestId = r.IsDBNull(r.GetOrdinal("request_id"))
                ? null
                : r.GetString(r.GetOrdinal("request_id")),
            UserId = r.GetString(r.GetOrdinal("user_id")),
            Description = r.GetString(r.GetOrdinal("reason")),
            Matches = DeserializeMatches(r.GetString(r.GetOrdinal("matches"))),
            Status = FromDb(r.GetString(r.GetOrdinal("status"))),
            CreatedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("flagged_at")),
            DecidedBy = r.IsDBNull(r.GetOrdinal("reviewer_id"))
                ? null
                : r.GetString(r.GetOrdinal("reviewer_id")),
            DecidedAt = r.IsDBNull(r.GetOrdinal("reviewed_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("reviewed_at")),
            DecisionNote = r.IsDBNull(r.GetOrdinal("decision_note"))
                ? null
                : r.GetString(r.GetOrdinal("decision_note")),
        };
    }

    private static string SerializeMatches(IReadOnlyList<ProhibitedItemMatch> matches) =>
        JsonSerializer.Serialize(matches, Json);

    private static IReadOnlyList<ProhibitedItemMatch> DeserializeMatches(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<ProhibitedItemMatch>>(json, Json)
                ?? new List<ProhibitedItemMatch>();
        }
        catch (JsonException)
        {
            return Array.Empty<ProhibitedItemMatch>();
        }
    }

    private static string ToDb(FlaggedRequestStatus status) => status.ToString().ToLowerInvariant();

    private static FlaggedRequestStatus FromDb(string status) =>
        Enum.TryParse<FlaggedRequestStatus>(status, ignoreCase: true, out var parsed)
            ? parsed
            : FlaggedRequestStatus.Pending;
}
