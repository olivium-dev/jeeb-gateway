using JeebGateway.Infrastructure;
using JeebGateway.Requests;
using JeebGateway.Tokens;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace JeebGateway.Users;

/// <summary>
/// Postgres-backed <see cref="IAccountDeletionStore"/> (jeeb-gateway durability
/// hardening, T-backend-035). Mirrors <see cref="JeebGateway.Financials.PostgresSettlementStore"/>'s
/// raw-Npgsql shape (connection-per-call via <see cref="INpgsqlConnectionFactory"/>,
/// named parameters, reader-based row mapping) and reuses
/// <see cref="JeebGateway.Availability.PostgresAvailabilityStore"/>'s
/// <c>@Param::enum_type</c> cast idiom for the native <c>account_deletion_status</c>
/// Postgres enum (an unmapped Postgres enum comes back over the wire as text, so
/// reads use plain <c>GetString</c> — no NpgsqlDataSource enum-mapper plugin
/// required). Replaces <see cref="InMemoryAccountDeletionStore"/> in production so
/// the GDPR-style deletion queue (pending_active_delivery → scheduled → completed)
/// survives a gateway bounce / replica move instead of resetting on every deploy —
/// exactly the gap a 30-day SLA cannot tolerate.
///
/// <para><b>Table reuse (never re-CREATE).</b> Persists to the EXISTING
/// <c>account_deletions</c> table (migration 0010) as-is. Every
/// <see cref="AccountDeletionRequest"/> field already has a column
/// (<c>user_id</c>, <c>status</c>, <c>anonymized_user_hash</c>, <c>requested_at</c>,
/// <c>scheduled_purge_at</c>, <c>completed_at</c>) — no ALTER migration was needed.</para>
///
/// <para><b>Idempotency shape.</b> <see cref="RequestAsync"/> mirrors
/// <c>PostgresSettlementStore.TryInsertAsync</c>'s insert-ONCE idiom
/// (<c>INSERT … ON CONFLICT (user_id) DO NOTHING RETURNING user_id</c>). A second
/// request for the same user returns the existing row UNCHANGED and skips every
/// side effect (token revocation, order/ledger anonymization) — exactly like
/// <see cref="InMemoryAccountDeletionStore"/>'s <c>created</c> guard.</para>
///
/// <para><b>State transitions.</b> <see cref="AdvanceAsync"/> mirrors
/// <c>PostgresSettlementStore.ReplacePendingAsync</c>'s state-guarded
/// <c>UPDATE … WHERE user_id = @UserId AND status = @ExpectedStatus</c> idiom, so two
/// overlapping worker ticks can never double-anonymize or double-purge the same row —
/// only the tick that actually flips the row's status runs the downstream side
/// effect. Ordering matches <see cref="InMemoryAccountDeletionStore.AdvanceAsync"/>
/// exactly: transition THEN anonymize for pending→scheduled (state committed first is
/// safe to redo); PII purge THEN transition for scheduled→completed (so a purge that
/// throws leaves the row retryable next tick instead of silently going terminal with
/// PII still present).</para>
///
/// <para><b>Hash / SLA reuse.</b> <see cref="InMemoryAccountDeletionStore.HashUserId"/>
/// and <see cref="InMemoryAccountDeletionStore.PurgeDelay"/> are reused as-is (not
/// duplicated) — migration 0010's own header notes that the SAME user id must always
/// hash to the SAME pseudonym so cross-table analytics joins survive deletion; a
/// second, independently-maintained hash implementation would risk drifting from that
/// invariant.</para>
/// </summary>
public sealed class PostgresAccountDeletionStore : IAccountDeletionStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly IUsersStore _users;
    private readonly IRequestsStore _requests;
    private readonly ITokenService _tokens;
    private readonly IFinancialLedgerAnonymizer _ledger;
    private readonly TimeProvider _clock;
    private readonly ILogger<PostgresAccountDeletionStore> _log;

    // GW12-PERF-2 (Leg-12): per-tick page size for the background sweep's list queries.
    // Both AdvanceAsync scans (pending→scheduled, scheduled→completed) walk the result
    // set making 2-3 sequential awaited DB/HTTP calls per row. Without a bound, a sweep
    // that falls behind (state-service / ledger degraded for a while) materializes the
    // ENTIRE backlog into memory and processes it serially in one tick. A LIMIT drains
    // the backlog incrementally across ticks instead. Each processed row changes status
    // (or is skipped and retried next tick), so the queries make forward progress and
    // eventually drain the whole backlog even without an ORDER BY.
    private const int SweepPageSize = 200;

    public PostgresAccountDeletionStore(
        INpgsqlConnectionFactory db,
        IUsersStore users,
        IRequestsStore requests,
        ITokenService tokens,
        IFinancialLedgerAnonymizer ledger,
        TimeProvider clock,
        ILogger<PostgresAccountDeletionStore> log)
    {
        _db = db;
        _users = users;
        _requests = requests;
        _tokens = tokens;
        _ledger = ledger;
        _clock = clock;
        _log = log;
    }

    public async Task<AccountDeletionRequest> RequestAsync(string userId, bool hasActiveDelivery, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        var status = hasActiveDelivery ? AccountDeletionStatus.PendingActiveDelivery : AccountDeletionStatus.Scheduled;
        DateTimeOffset? scheduledPurgeAt = hasActiveDelivery ? null : now + InMemoryAccountDeletionStore.PurgeDelay;
        var hash = InMemoryAccountDeletionStore.HashUserId(userId);

        // F7: account_deletions.user_id is a UUID FK; a non-GUID X-User-Id (the UM-down
        // MVP fallback identity) can never be persisted. Degrade instead of 500ing on
        // Guid.Parse: still revoke tokens + anonymize (both accept the string id) so the
        // security/GDPR side effects run, and return a synthesized (non-durable) record —
        // the permissive posture the in-memory store had.
        if (!Guid.TryParse(userId, out var userGuid))
        {
            _log.LogWarning(
                "Account-deletion requested for non-GUID userId={UserId}; running side effects without a durable row (MVP fallback identity).",
                userId);
            await _tokens.RevokeAllForUserAsync(userId, RevocationReason.AccountDeleted, ct);
            if (status == AccountDeletionStatus.Scheduled)
            {
                await _requests.AnonymizeForClientAsync(userId, hash, ct);
                await _ledger.AnonymizeForUserAsync(userId, hash, ct);
            }
            return new AccountDeletionRequest
            {
                UserId = userId,
                Status = status,
                RequestedAt = now,
                ScheduledPurgeAt = scheduledPurgeAt,
                AnonymizedUserHash = hash
            };
        }

        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO account_deletions (
                user_id, status, anonymized_user_hash, requested_at, scheduled_purge_at
            ) VALUES (
                @UserId, @Status::account_deletion_status, @AnonymizedUserHash, @RequestedAt, @ScheduledPurgeAt
            )
            ON CONFLICT (user_id) DO NOTHING
            RETURNING user_id
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("UserId", userGuid);
        insertCmd.Parameters.AddWithValue("Status", status);
        insertCmd.Parameters.AddWithValue("AnonymizedUserHash", hash);
        insertCmd.Parameters.AddWithValue("RequestedAt", now);
        insertCmd.Parameters.AddWithValue("ScheduledPurgeAt", (object?)scheduledPurgeAt ?? DBNull.Value);

        var inserted = await insertCmd.ExecuteScalarAsync(ct) is not null;

        if (inserted)
        {
            _log.LogInformation(
                "Account-deletion requested userId={UserId} status={Status} scheduledPurgeAt={ScheduledPurgeAt}",
                userId, status, scheduledPurgeAt);
        }
        else
        {
            _log.LogDebug(
                "Account-deletion request already exists for userId={UserId}; ensuring side effects completed", userId);
        }

        // F5: the row now exists (either we just inserted it, or a prior request did).
        // Read it back WITH its side-effects marker and run the side effects only if
        // they have not been marked complete. This makes RequestAsync crash-safe: if a
        // process died after the INSERT commit but before the side effects finished, the
        // retry re-enters here, sees the marker still NULL, and re-runs the (idempotent)
        // token revocation + anonymization — instead of the previous behaviour where the
        // ON CONFLICT DO NOTHING made the retry skip them forever. A genuine duplicate
        // request (marker already set) still skips them, preserving idempotency.
        var (existing, sideEffectsDone) = await GetRowWithMarkerAsync(conn, userGuid, ct);
        if (existing is null)
        {
            // Extremely narrow race: the row we just inserted / conflicted on was hard-
            // purged between the insert and this read. Fall back to the synthesized shape.
            existing = new AccountDeletionRequest
            {
                UserId = userId,
                Status = status,
                RequestedAt = now,
                ScheduledPurgeAt = scheduledPurgeAt,
                AnonymizedUserHash = hash
            };
            sideEffectsDone = false;
        }

        if (!sideEffectsDone)
        {
            // Whether or not the purge clock has started, the user asked us to delete
            // their account — every refresh token they hold must be invalidated
            // immediately so no other device can keep using the account.
            await _tokens.RevokeAllForUserAsync(userId, RevocationReason.AccountDeleted, ct);

            // Landed directly in `scheduled` (no active delivery) — anonymize the order +
            // financial-ledger rows now so analytics joins still work but the user-id
            // linkage is gone. Re-runnable: anonymize-by-hash is idempotent.
            if (existing.Status == AccountDeletionStatus.Scheduled)
            {
                await _requests.AnonymizeForClientAsync(userId, existing.AnonymizedUserHash, ct);
                await _ledger.AnonymizeForUserAsync(userId, existing.AnonymizedUserHash, ct);
            }

            // Mark side effects complete LAST, guarded so a concurrent retry that also
            // ran them collapses onto a single marker write.
            await MarkSideEffectsCompleteAsync(userGuid, now, ct);
        }

        return existing;
    }

    /// <summary>
    /// F5: reads the deletion row together with whether its request side effects have
    /// been marked complete (<c>side_effects_completed_at IS NOT NULL</c>).
    /// </summary>
    private static async Task<(AccountDeletionRequest? Row, bool SideEffectsDone)> GetRowWithMarkerAsync(
        NpgsqlConnection conn, Guid userId, CancellationToken ct)
    {
        const string sql = """
            SELECT *, (side_effects_completed_at IS NOT NULL) AS side_effects_done
            FROM account_deletions WHERE user_id = @UserId LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return (null, false);
        }
        var done = reader.GetBoolean(reader.GetOrdinal("side_effects_done"));
        return (MapRow(reader), done);
    }

    /// <summary>
    /// F5: idempotently stamps the request side-effects completion marker. The
    /// <c>WHERE ... IS NULL</c> guard makes a concurrent double-run collapse to one write.
    /// </summary>
    private async Task MarkSideEffectsCompleteAsync(Guid userId, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE account_deletions
            SET side_effects_completed_at = @At
            WHERE user_id = @UserId AND side_effects_completed_at IS NULL
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        cmd.Parameters.AddWithValue("At", at);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct)
    {
        // F7: a non-GUID id (MVP fallback identity) can never have a durable row —
        // return no-match instead of letting Guid.Parse throw a 500.
        if (!Guid.TryParse(userId, out var userGuid)) return null;

        await using var conn = await _db.OpenAsync(ct);
        return await QuerySingleAsync(conn, userGuid, ct);
    }

    public async Task AdvanceAsync(DateTimeOffset now, CancellationToken ct)
    {
        // ── pending_active_delivery → scheduled ────────────────────────────────
        var toSchedule = await ListByStatusAsync(AccountDeletionStatus.PendingActiveDelivery, ct);
        foreach (var record in toSchedule)
        {
            var active = await _requests.CountActiveForClientAsync(record.UserId, ct);
            if (active > 0) continue;

            // F5: anonymize BEFORE the terminal flip, mirroring the scheduled→completed
            // ordering below. Previously the flip ran first, so a crash between the flip
            // and the anonymize left the row `scheduled` (never re-selected by this
            // pending sweep) with its orders/ledger un-anonymized forever. Anonymize-by-
            // hash is idempotent, so running it before the state-guarded flip — even if
            // two ticks race and both anonymize — is safe; only one tick wins the flip.
            await _requests.AnonymizeForClientAsync(record.UserId, record.AnonymizedUserHash, ct);
            await _ledger.AnonymizeForUserAsync(record.UserId, record.AnonymizedUserHash, ct);

            var scheduledPurgeAt = now + InMemoryAccountDeletionStore.PurgeDelay;
            var advanced = await TryAdvanceStatusAsync(
                record.UserId,
                fromStatus: AccountDeletionStatus.PendingActiveDelivery,
                toStatus: AccountDeletionStatus.Scheduled,
                scheduledPurgeAt: scheduledPurgeAt,
                completedAt: null,
                ct: ct);
            if (!advanced) continue; // lost the race to a concurrent tick — next tick re-evaluates

            _log.LogInformation(
                "Account-deletion userId={UserId} advanced pending_active_delivery→scheduled scheduledPurgeAt={ScheduledPurgeAt}",
                record.UserId, scheduledPurgeAt);
        }

        // ── scheduled → completed (past SLA) ───────────────────────────────────
        var toComplete = await ListDueForPurgeAsync(now, ct);
        foreach (var record in toComplete)
        {
            // Purge PII BEFORE transitioning: if PurgePiiAsync throws, the row stays
            // `scheduled` so the next tick retries the purge instead of silently
            // landing `completed` with PII still present.
            await _users.PurgePiiAsync(record.UserId, ct);
            var advanced = await TryAdvanceStatusAsync(
                record.UserId,
                fromStatus: AccountDeletionStatus.Scheduled,
                toStatus: AccountDeletionStatus.Completed,
                scheduledPurgeAt: record.ScheduledPurgeAt,
                completedAt: now,
                ct: ct);

            if (advanced)
            {
                _log.LogInformation(
                    "Account-deletion userId={UserId} completed (PII purged) at {CompletedAt}",
                    record.UserId, now);
            }
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<List<AccountDeletionRequest>> ListByStatusAsync(string status, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // GW12-PERF-2: bounded page so a backlog drains incrementally across ticks
        // rather than being materialized in one unbounded batch.
        const string sql =
            "SELECT * FROM account_deletions WHERE status = @Status::account_deletion_status LIMIT @Limit";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Status", status);
        cmd.Parameters.AddWithValue("Limit", SweepPageSize);
        return await ReadListAsync(cmd, ct);
    }

    private async Task<List<AccountDeletionRequest>> ListDueForPurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // 'scheduled' is inlined as a literal (not parameterized) — this query only
        // ever runs against the one fixed status, same as
        // PostgresSettlementStore.ListRecordedInWindowAsync's `cod_state = 'recorded'`.
        // GW12-PERF-2: bounded page so a purge backlog drains incrementally across ticks.
        const string sql = """
            SELECT * FROM account_deletions
            WHERE status = 'scheduled'::account_deletion_status AND scheduled_purge_at <= @Now
            LIMIT @Limit
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Now", now);
        cmd.Parameters.AddWithValue("Limit", SweepPageSize);
        return await ReadListAsync(cmd, ct);
    }

    /// <summary>
    /// State-guarded transition — mirrors <c>PostgresSettlementStore.ReplacePendingAsync</c>'s
    /// <c>WHERE … AND state = @ExpectedState</c> idiom. Returns false (no-op) when the
    /// row already moved on, so two overlapping <see cref="AdvanceAsync"/> ticks can
    /// never both run the same downstream side effect for the same row.
    /// <c>updated_at</c> is refreshed by the 0010 <c>account_deletions_set_updated_at</c>
    /// trigger — not set here.
    /// </summary>
    private async Task<bool> TryAdvanceStatusAsync(
        string userId, string fromStatus, string toStatus,
        DateTimeOffset? scheduledPurgeAt, DateTimeOffset? completedAt, CancellationToken ct)
    {
        // F7: never throw on a non-GUID id — such a row can't exist, so the guarded
        // transition is a no-op (returns false, caller re-evaluates next tick).
        if (!Guid.TryParse(userId, out var userGuid)) return false;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE account_deletions
            SET status = @ToStatus::account_deletion_status,
                scheduled_purge_at = @ScheduledPurgeAt,
                completed_at = @CompletedAt
            WHERE user_id = @UserId AND status = @FromStatus::account_deletion_status
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userGuid);
        cmd.Parameters.AddWithValue("ToStatus", toStatus);
        cmd.Parameters.AddWithValue("FromStatus", fromStatus);
        cmd.Parameters.AddWithValue("ScheduledPurgeAt", (object?)scheduledPurgeAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("CompletedAt", (object?)completedAt ?? DBNull.Value);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    private static async Task<AccountDeletionRequest?> QuerySingleAsync(NpgsqlConnection conn, Guid userId, CancellationToken ct)
    {
        const string sql = "SELECT * FROM account_deletions WHERE user_id = @UserId LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<List<AccountDeletionRequest>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<AccountDeletionRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static AccountDeletionRequest MapRow(NpgsqlDataReader r) => new()
    {
        UserId = r.GetGuid(r.GetOrdinal("user_id")).ToString(),
        // Unmapped Postgres enum comes back over the wire as text — GetString works
        // with no enum type-mapper plugin required (same reason the writes above cast
        // @Param::account_deletion_status in SQL text; see PostgresAvailabilityStore).
        Status = r.GetString(r.GetOrdinal("status")),
        RequestedAt = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("requested_at")),
        ScheduledPurgeAt = r.IsDBNull(r.GetOrdinal("scheduled_purge_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("scheduled_purge_at")),
        CompletedAt = r.IsDBNull(r.GetOrdinal("completed_at"))
            ? null
            : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("completed_at")),
        AnonymizedUserHash = r.GetString(r.GetOrdinal("anonymized_user_hash")),
    };
}
