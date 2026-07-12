using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace JeebGateway.Financials;

/// <summary>
/// Postgres-backed <see cref="ISettlementStore"/> (JEB-56, TL-PIN-JEB-510).
///
/// Replaces <see cref="InMemorySettlementStore"/> in production. Idempotency on
/// <c>delivery_id</c> is enforced at the DB level via <c>UNIQUE(delivery_id)</c> +
/// <c>INSERT … ON CONFLICT DO NOTHING</c> so concurrent retries converge safely.
///
/// All monetary values use NUMERIC(20,4) — no float arithmetic at any layer.
/// </summary>
public sealed class PostgresSettlementStore : ISettlementStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ILogger<PostgresSettlementStore> _log;

    public PostgresSettlementStore(INpgsqlConnectionFactory db, ILogger<PostgresSettlementStore> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<(Settlement Row, bool Inserted)> TryInsertAsync(Settlement settlement, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);

        const string insertSql = """
            INSERT INTO settlements (
                id, delivery_id, jeeber_id, client_id, tier_id,
                goods_cost, commission_rate, commission, insurance, total,
                min_fee_applied, currency, payment_method,
                state, cod_state,
                settled_at, created_at, updated_at
            ) VALUES (
                @Id, @DeliveryId, @JeeberId, @ClientId, @TierId,
                @GoodsCost, @CommissionRate, @Commission, @Insurance, @Total,
                @MinFeeApplied, @Currency, @PaymentMethod,
                @State, @CodState,
                @SettledAt, now(), now()
            )
            ON CONFLICT (delivery_id) DO NOTHING
            RETURNING id
            """;

        await using var insertCmd = new NpgsqlCommand(insertSql, conn);
        insertCmd.Parameters.AddWithValue("Id", Guid.Parse(settlement.Id));
        insertCmd.Parameters.AddWithValue("DeliveryId", settlement.DeliveryId);
        insertCmd.Parameters.AddWithValue("JeeberId", settlement.JeeberId);
        insertCmd.Parameters.AddWithValue("ClientId", settlement.ClientId);
        insertCmd.Parameters.AddWithValue("TierId", settlement.TierId);
        insertCmd.Parameters.AddWithValue("GoodsCost", settlement.GoodsCost);
        insertCmd.Parameters.AddWithValue("CommissionRate", settlement.CommissionRate);
        insertCmd.Parameters.AddWithValue("Commission", settlement.Commission);
        insertCmd.Parameters.AddWithValue("Insurance", settlement.Insurance);
        insertCmd.Parameters.AddWithValue("Total", settlement.Total);
        insertCmd.Parameters.AddWithValue("MinFeeApplied", settlement.MinimumFeeApplied);
        insertCmd.Parameters.AddWithValue("Currency", settlement.Currency);
        insertCmd.Parameters.AddWithValue("PaymentMethod", settlement.PaymentMethod);
        insertCmd.Parameters.AddWithValue("State", settlement.State);
        insertCmd.Parameters.AddWithValue("CodState", settlement.CodState);
        insertCmd.Parameters.AddWithValue("SettledAt", settlement.SettledAt);

        var inserted = await insertCmd.ExecuteScalarAsync(ct) is not null;

        if (inserted)
        {
            _log.LogInformation(
                "Settlement recorded deliveryId={DeliveryId} settlementId={Id} codState=recorded",
                settlement.DeliveryId, settlement.Id);
            return (settlement, true);
        }

        // Conflict — return the existing row.
        var existing = await GetByDeliveryAsync(settlement.DeliveryId, ct);
        _log.LogDebug(
            "Settlement already exists for deliveryId={DeliveryId}; returning existing row",
            settlement.DeliveryId);
        return (existing!, false);
    }

    public async Task<Settlement?> GetByDeliveryAsync(string deliveryId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await QuerySingleAsync(conn, "WHERE delivery_id = @p", "@p", deliveryId, ct);
    }

    public async Task<Settlement?> GetByIdAsync(string settlementId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        return await QuerySingleAsync(conn, "WHERE id = @p", "@p", Guid.Parse(settlementId), ct);
    }

    public async Task<IReadOnlyList<Settlement>> ListByJeeberAsync(
        string jeeberId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct,
        IReadOnlyCollection<string>? codStates = null)
    {
        await using var conn = await _db.OpenAsync(ct);

        // JEB-58: earnings query uses idx_settlements_jeeber_state_settled.
        // When codStates is specified, use ANY(@CodStates) to stay on the index.
        var sql = codStates is { Count: > 0 }
            ? """
              SELECT * FROM settlements
              WHERE jeeber_id = @JeeberId
                AND cod_state = ANY(@CodStates)
                AND (@From IS NULL OR settled_at >= @From)
                AND (@To   IS NULL OR settled_at <= @To)
              ORDER BY settled_at ASC
              """
            : """
              SELECT * FROM settlements
              WHERE jeeber_id = @JeeberId
                AND (@From IS NULL OR settled_at >= @From)
                AND (@To   IS NULL OR settled_at <= @To)
              ORDER BY settled_at ASC
              """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("JeeberId", jeeberId);
        // JEBV4-283: type the window bounds explicitly as timestamptz. AddWithValue on a
        // DBNull carries NO type, so on the lifetime path (from/to == null) Postgres cannot
        // infer the parameter type for the `@From IS NULL` / `@To IS NULL` predicates and the
        // query fails with 42P08 "could not determine data type of parameter $2". An explicit
        // NpgsqlDbType.TimestampTz makes the null strongly-typed; a real DateTimeOffset value is
        // sent as timestamptz either way (matches the settled_at column type).
        cmd.Parameters.Add(new NpgsqlParameter("From", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)from ?? DBNull.Value,
        });
        cmd.Parameters.Add(new NpgsqlParameter("To", NpgsqlDbType.TimestampTz)
        {
            Value = (object?)to ?? DBNull.Value,
        });
        if (codStates is { Count: > 0 })
            cmd.Parameters.AddWithValue("CodStates", codStates.ToArray());

        return await ReadListAsync(cmd, ct);
    }

    public async Task<bool> ReplacePendingAsync(string deliveryId, Settlement settled, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE settlements
               SET id            = @NewId,
                   goods_cost    = @GoodsCost,
                   commission    = @Commission,
                   commission_rate = @CommissionRate,
                   insurance     = @Insurance,
                   total         = @Total,
                   min_fee_applied = @MinimumFeeApplied,
                   payment_method = @PaymentMethod,
                   state         = @State,
                   cod_state     = @CodState,
                   settled_at    = @SettledAt,
                   batch_id      = NULL,
                   batched_at    = NULL
             WHERE delivery_id = @DeliveryId
               AND state = 'pending_settlement'
            """;
        cmd.Parameters.AddWithValue("NewId", Guid.Parse(settled.Id));
        cmd.Parameters.AddWithValue("DeliveryId", deliveryId);
        cmd.Parameters.AddWithValue("GoodsCost", settled.GoodsCost);
        cmd.Parameters.AddWithValue("Commission", settled.Commission);
        cmd.Parameters.AddWithValue("CommissionRate", settled.CommissionRate);
        cmd.Parameters.AddWithValue("Insurance", settled.Insurance);
        cmd.Parameters.AddWithValue("Total", settled.Total);
        cmd.Parameters.AddWithValue("MinimumFeeApplied", settled.MinimumFeeApplied);
        cmd.Parameters.AddWithValue("PaymentMethod", settled.PaymentMethod);
        cmd.Parameters.AddWithValue("State", settled.State);
        cmd.Parameters.AddWithValue("CodState", settled.CodState);
        cmd.Parameters.AddWithValue("SettledAt", settled.SettledAt);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        return rows > 0;
    }

    public async Task<bool> SetLedgerEntryAsync(string settlementId, string ledgerEntryId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE settlements
            SET ledger_entry_id = @LedgerEntryId, updated_at = now()
            WHERE id = @Id
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", Guid.Parse(settlementId));
        cmd.Parameters.AddWithValue("LedgerEntryId", ledgerEntryId);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<Settlement?> MarkReceiptGeneratedAsync(string settlementId, DateTimeOffset at, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // M1 (P0 money-loss) — state-machine guard: ONLY a truly-settled row may advance
        // to receipt_generated. The transition is `settled → receipt_generated` and nothing
        // else. Previously the guard was `state != @NewState`, which happily advanced a
        // 'pending_settlement' HANDOVER PLACEHOLDER (goods_cost=0, min-fee phantom commission,
        // NO ledger entry — written by DeliveriesController) straight to receipt_generated the
        // first time ANY party read the receipt BEFORE POST /settle ran. That phantom row then
        // (a) satisfied the weekly payout batch selector and underpaid the Jeeber, and
        // (b) made SettlementService.SettleAsync short-circuit AlreadySettled on a non-pending
        // state, so the real commission math + ledger post never ran. Pinning the WHERE to
        // `state = 'settled'` slams that escape hatch shut: a placeholder stays
        // 'pending_settlement' until a real settle (ReplacePendingAsync) advances it to
        // 'settled', and only THEN can a receipt read advance it to receipt_generated.
        const string sql = """
            UPDATE settlements
            SET state = @NewState, receipt_generated_at = @At, updated_at = now()
            WHERE id = @Id AND state = @SettledState
            RETURNING *
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", Guid.Parse(settlementId));
        cmd.Parameters.AddWithValue("NewState", SettlementState.ReceiptGenerated);
        cmd.Parameters.AddWithValue("SettledState", SettlementState.Settled);
        cmd.Parameters.AddWithValue("At", at);

        var results = await ReadListAsync(cmd, ct);
        if (results.Count > 0) return results[0];

        // Not in 'settled' state: either already receipt_generated (idempotent repeat read —
        // return the existing row unchanged) or still a pending_settlement placeholder (a
        // pre-settle receipt read does NOT advance it). Either way, return the row as-is.
        return await GetByIdAsync(settlementId, ct);
    }

    public async Task<IReadOnlyList<Settlement>> ListRecordedInWindowAsync(
        DateTimeOffset windowStart, DateTimeOffset windowEnd, int limit, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        // M1 (P0 money-loss): only TRULY-settled rows may enter the weekly payout batch.
        // The handover placeholder is written with state='pending_settlement', cod_state='recorded'
        // and goods_cost=0. Sweeping placeholders into payout batches underpays the Jeeber
        // and books commission never collected.
        //
        // The selector POSITIVELY requires a completed settlement: state IN ('settled',
        // 'receipt_generated'). A row only reaches those states through SettlementService.SettleAsync
        // (ReplacePendingAsync / TryInsertAsync sets 'settled') and, optionally, a post-settle
        // receipt read ('receipt_generated'). A placeholder is 'pending_settlement' and is now
        // ALSO barred from advancing to receipt_generated pre-settle (see MarkReceiptGeneratedAsync
        // guard), so it can never satisfy this predicate — closing the receipt-before-settle escape
        // hatch belt-and-suspenders with that guard. We key on state rather than
        // `ledger_entry_id IS NOT NULL` because the ledger post is best-effort/replayed
        // (SettlementService.cs) — a genuinely-settled row whose ledger post is still pending would
        // be wrongly WITHHELD from payout by a ledger predicate; state is the gateway's authoritative
        // "settlement complete" signal (the row is the system of record). Also closes M2: an
        // unbatched placeholder can never be re-swept after ReplacePendingAsync.
        const string sql = """
            SELECT * FROM settlements
            WHERE cod_state = 'recorded'
              AND state IN ('settled', 'receipt_generated')
              AND settled_at >= @WindowStart
              AND settled_at < @WindowEnd
            ORDER BY settled_at ASC
            LIMIT @Limit
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("WindowStart", windowStart);
        cmd.Parameters.AddWithValue("WindowEnd", windowEnd);
        cmd.Parameters.AddWithValue("Limit", limit);
        return await ReadListAsync(cmd, ct);
    }

    public async Task MarkBatchedAsync(
        IReadOnlyList<string> settlementIds, Guid batchId, DateTimeOffset at, CancellationToken ct)
    {
        if (settlementIds.Count == 0) return;

        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE settlements
            SET cod_state = 'batched', batch_id = @BatchId, batched_at = @At, updated_at = now()
            WHERE id = ANY(@Ids) AND cod_state = 'recorded'
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("BatchId", batchId);
        cmd.Parameters.AddWithValue("At", at);
        cmd.Parameters.AddWithValue("Ids", settlementIds.Select(Guid.Parse).ToArray());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarkPaidByBatchAsync(Guid batchId, DateTimeOffset paidAt, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = """
            UPDATE settlements
            SET cod_state = 'paid', paid_at = @PaidAt, updated_at = now()
            WHERE batch_id = @BatchId AND cod_state = 'batched'
            """;
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("BatchId", batchId);
        cmd.Parameters.AddWithValue("PaidAt", paidAt);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<Settlement?> QuerySingleAsync(
        NpgsqlConnection conn, string whereClause, string paramName, object paramValue, CancellationToken ct)
    {
        var sql = $"SELECT * FROM settlements {whereClause} LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue(paramName, paramValue);
        var rows = await ReadListAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<List<Settlement>> ReadListAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<Settlement>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(MapRow(reader));
        }
        return results;
    }

    private static Settlement MapRow(NpgsqlDataReader r)
    {
        var commissionTierText = r.IsDBNull(r.GetOrdinal("tier_id"))
            ? string.Empty
            : r.GetString(r.GetOrdinal("tier_id"));

        return new Settlement
        {
            Id             = r.GetGuid(r.GetOrdinal("id")).ToString(),
            DeliveryId     = r.GetString(r.GetOrdinal("delivery_id")),
            JeeberId       = r.GetString(r.GetOrdinal("jeeber_id")),
            ClientId       = r.GetString(r.GetOrdinal("client_id")),
            TierId         = commissionTierText,
            GoodsCost      = r.GetDecimal(r.GetOrdinal("goods_cost")),
            CommissionTier = CommissionCalculator.ResolveTier(commissionTierText),
            CommissionRate = r.GetDecimal(r.GetOrdinal("commission_rate")),
            Commission     = r.GetDecimal(r.GetOrdinal("commission")),
            Insurance      = r.GetDecimal(r.GetOrdinal("insurance")),
            Total          = r.GetDecimal(r.GetOrdinal("total")),
            MinimumFeeApplied = r.GetBoolean(r.GetOrdinal("min_fee_applied")),
            Currency       = r.GetString(r.GetOrdinal("currency")),
            PaymentMethod  = r.GetString(r.GetOrdinal("payment_method")),
            State          = r.GetString(r.GetOrdinal("state")),
            CodState       = r.GetString(r.GetOrdinal("cod_state")),
            SettledAt      = r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("settled_at")),
            ReceiptGeneratedAt = r.IsDBNull(r.GetOrdinal("receipt_generated_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("receipt_generated_at")),
            LedgerEntryId = r.IsDBNull(r.GetOrdinal("ledger_entry_id"))
                ? null
                : r.GetString(r.GetOrdinal("ledger_entry_id")),
            BatchId   = r.IsDBNull(r.GetOrdinal("batch_id"))
                ? null
                : r.GetGuid(r.GetOrdinal("batch_id")),
            BatchedAt = r.IsDBNull(r.GetOrdinal("batched_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("batched_at")),
            PaidAt    = r.IsDBNull(r.GetOrdinal("paid_at"))
                ? null
                : r.GetFieldValue<DateTimeOffset>(r.GetOrdinal("paid_at")),
        };
    }
}
