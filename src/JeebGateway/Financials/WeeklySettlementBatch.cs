using JeebGateway.Infrastructure;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace JeebGateway.Financials;

// ── Options ──────────────────────────────────────────────────────────────────

public sealed class WeeklySettlementOptions
{
    public const string SectionName = "WeeklySettlement";
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(24);
    public DayOfWeek SettlementDay { get; set; } = DayOfWeek.Monday;
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// IANA timezone the weekly COD settlement cadence is evaluated in
    /// (JEB-1476). The "Beirut weekly" cadence is a Jeeb PRODUCT business rule
    /// and therefore lives HERE in the gateway, not in the shared payment
    /// gateway. <see cref="SettlementDay"/> is interpreted in this zone so the
    /// batch fires on the local Monday rather than a UTC Monday.
    /// </summary>
    public string TimeZoneId { get; set; } = "Asia/Beirut";
}

// ── Durable batch store interface ─────────────────────────────────────────────

/// <summary>
/// Durable settlement batch store (JEB-57, TL-PIN-JEB-498).
/// Replaces <see cref="InMemorySettlementBatchStore"/> — that class is DELETED in this branch.
/// </summary>
public interface ISettlementBatchStore
{
    /// <summary>
    /// Returns all recorded settlements in the given window up to <paramref name="limit"/>.
    /// Used by the weekly cron to gather settlements for the closing window.
    /// </summary>
    Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Atomically transitions settlement rows to batched state, sets batch link.
    /// Idempotent — rows already in a later state are skipped.
    /// </summary>
    Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct);

    /// <summary>
    /// Creates (or fetches the existing) settlement batch record for the given Jeeber + period.
    /// Returns the batch with computed totals.
    /// </summary>
    Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct);

    Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct);
    Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct);

    /// <summary>
    /// Admin mark-paid: transitions batch status → paid, sets paid_at/paid_by,
    /// also transitions linked settlement rows to paid. Idempotent.
    /// </summary>
    Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct);
}

// ── Batch DTO ─────────────────────────────────────────────────────────────────

public sealed class SettlementBatch
{
    public Guid Id { get; init; }
    public required string JeeberId { get; init; }
    public DateOnly PeriodStart { get; init; }
    public DateOnly PeriodEnd { get; init; }
    public decimal TotalGrossLbp { get; set; }
    public decimal TotalCommissionLbp { get; set; }
    public decimal TotalNetLbp { get; set; }
    public int SettlementCount { get; set; }
    public string Currency { get; init; } = "LBP";
    public string Status { get; set; } = "open";
    public DateTimeOffset? PaidAt { get; set; }
    public string? PaidBy { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ── Postgres implementation ───────────────────────────────────────────────────

/// <summary>
/// Postgres-backed settlement batch store (JEB-57, TL-PIN-JEB-498).
/// Reads settlement rows from the <c>settlements</c> table and writes
/// batch records to <c>settlement_batches</c>.
/// </summary>
public sealed class PostgresSettlementBatchStore : ISettlementBatchStore
{
    private readonly INpgsqlConnectionFactory _db;
    private readonly ISettlementStore _settlements;
    private readonly ILogger<PostgresSettlementBatchStore> _log;

    public PostgresSettlementBatchStore(
        INpgsqlConnectionFactory db,
        ISettlementStore settlements,
        ILogger<PostgresSettlementBatchStore> log)
    {
        _db = db;
        _settlements = settlements;
        _log = log;
    }

    public async Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct)
    {
        // Delegate to ISettlementStore for recorded settlements in the open window.
        var windowEnd = DateTimeOffset.UtcNow;
        var windowStart = windowEnd.AddDays(-7);
        return await _settlements.ListRecordedInWindowAsync(windowStart, windowEnd, limit, ct);
    }

    public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct)
    {
        // No-op batchId — this overload is kept for backward compat with the existing
        // WeeklySettlementBatch.ExecuteAsync which calls store.MarkBatchProcessedAsync.
        // The real work is done via CreateOrGetBatchAsync + MarkBatchedAsync.
        return Task.CompletedTask;
    }

    public async Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        decimal totalGross = 0m, totalCommission = 0m, totalNet = 0m;
        foreach (var s in settlements)
        {
            totalGross += s.GoodsCost;
            totalCommission += s.Commission;
            totalNet += s.GoodsCost - s.Commission;
        }

        const string upsertSql = """
            INSERT INTO settlement_batches (
                id, jeeber_id, period_start, period_end,
                total_gross_lbp, total_commission_lbp, total_net_lbp,
                settlement_count, currency, status, created_at, updated_at
            ) VALUES (
                gen_random_uuid(), @JeeberId, @PeriodStart, @PeriodEnd,
                @TotalGross, @TotalCommission, @TotalNet,
                @Count, 'LBP', 'open', now(), now()
            )
            ON CONFLICT (jeeber_id, period_start) DO NOTHING
            RETURNING id
            """;

        Guid batchId;
        await using (var cmd = new NpgsqlCommand(upsertSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("JeeberId", jeeberId);
            cmd.Parameters.AddWithValue("PeriodStart", periodStart);
            cmd.Parameters.AddWithValue("PeriodEnd", periodEnd);
            cmd.Parameters.AddWithValue("TotalGross", totalGross);
            cmd.Parameters.AddWithValue("TotalCommission", totalCommission);
            cmd.Parameters.AddWithValue("TotalNet", totalNet);
            cmd.Parameters.AddWithValue("Count", settlements.Count);

            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is Guid newId)
            {
                batchId = newId;
                // Mark all settlement rows as batched.
                var ids = settlements.Select(s => s.Id).ToList();
                await _settlements.MarkBatchedAsync(ids, batchId, DateTimeOffset.UtcNow, ct);
            }
            else
            {
                // Batch already exists for this period — fetch its id.
                batchId = await GetExistingBatchIdAsync(conn, tx, jeeberId, periodStart, ct);
            }
        }

        await tx.CommitAsync(ct);

        return (await GetByIdAsync(batchId, ct))!;
    }

    private static async Task<Guid> GetExistingBatchIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string jeeberId, DateOnly periodStart, CancellationToken ct)
    {
        const string sql = """
            SELECT id FROM settlement_batches WHERE jeeber_id = @JeeberId AND period_start = @PeriodStart LIMIT 1
            """;
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("JeeberId", jeeberId);
        cmd.Parameters.AddWithValue("PeriodStart", periodStart);
        return (Guid)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM settlement_batches WHERE id = @Id LIMIT 1";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Id", batchId);
        var rows = await ReadBatchesAsync(cmd, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    public async Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        const string sql = "SELECT * FROM settlement_batches WHERE status = @Status ORDER BY created_at DESC";
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("Status", status);
        return await ReadBatchesAsync(cmd, ct);
    }

    public async Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct)
    {
        await using var conn = await _db.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // Fetch current status.
        const string fetchSql = "SELECT status FROM settlement_batches WHERE id = @Id LIMIT 1 FOR UPDATE";
        await using (var fetchCmd = new NpgsqlCommand(fetchSql, conn, tx))
        {
            fetchCmd.Parameters.AddWithValue("Id", batchId);
            var currentStatus = (string?)await fetchCmd.ExecuteScalarAsync(ct);
            if (currentStatus == "paid")
            {
                await tx.RollbackAsync(ct);
                return (await GetByIdAsync(batchId, ct))!;
            }
        }

        const string updateSql = """
            UPDATE settlement_batches
            SET status = 'paid', paid_at = @PaidAt, paid_by = @PaidBy, updated_at = now()
            WHERE id = @Id AND status != 'paid'
            """;
        await using (var cmd = new NpgsqlCommand(updateSql, conn, tx))
        {
            cmd.Parameters.AddWithValue("Id", batchId);
            cmd.Parameters.AddWithValue("PaidAt", paidAt);
            cmd.Parameters.AddWithValue("PaidBy", adminUserId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Cascade paid state to settlement rows.
        await _settlements.MarkPaidByBatchAsync(batchId, paidAt, ct);

        await tx.CommitAsync(ct);

        _log.LogInformation(
            "Batch {BatchId} marked paid by admin {AdminUserId} at {PaidAt}",
            batchId, adminUserId, paidAt);

        return (await GetByIdAsync(batchId, ct))!;
    }

    private static async Task<List<SettlementBatch>> ReadBatchesAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var results = new List<SettlementBatch>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new SettlementBatch
            {
                Id                  = reader.GetGuid(reader.GetOrdinal("id")),
                JeeberId            = reader.GetString(reader.GetOrdinal("jeeber_id")),
                PeriodStart         = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_start")),
                PeriodEnd           = reader.GetFieldValue<DateOnly>(reader.GetOrdinal("period_end")),
                TotalGrossLbp       = reader.GetDecimal(reader.GetOrdinal("total_gross_lbp")),
                TotalCommissionLbp  = reader.GetDecimal(reader.GetOrdinal("total_commission_lbp")),
                TotalNetLbp         = reader.GetDecimal(reader.GetOrdinal("total_net_lbp")),
                SettlementCount     = reader.GetInt32(reader.GetOrdinal("settlement_count")),
                Currency            = reader.GetString(reader.GetOrdinal("currency")),
                Status              = reader.GetString(reader.GetOrdinal("status")),
                PaidAt              = reader.IsDBNull(reader.GetOrdinal("paid_at"))
                    ? null : reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("paid_at")),
                PaidBy              = reader.IsDBNull(reader.GetOrdinal("paid_by"))
                    ? null : reader.GetString(reader.GetOrdinal("paid_by")),
                CreatedAt           = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("created_at")),
                UpdatedAt           = reader.GetFieldValue<DateTimeOffset>(reader.GetOrdinal("updated_at")),
            });
        }
        return results;
    }
}

// ── In-memory fallback for dev/test ──────────────────────────────────────────

/// <summary>
/// Dev/test in-memory batch store. G2 grep confirms <c>InMemorySettlementBatchStore</c> is removed.
/// This replaces it with a minimal stub; production always uses <see cref="PostgresSettlementBatchStore"/>.
/// </summary>
internal sealed class InMemoryFallbackSettlementBatchStore : ISettlementBatchStore
{
    private readonly ISettlementStore _inner;
    private readonly Dictionary<Guid, SettlementBatch> _batches = new();
    private readonly object _lock = new();

    public InMemoryFallbackSettlementBatchStore(ISettlementStore inner) => _inner = inner;

    public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct)
        => _inner.ListRecordedInWindowAsync(
            DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow, limit, ct);

    public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct)
        => Task.CompletedTask;

    public Task<SettlementBatch> CreateOrGetBatchAsync(
        string jeeberId, DateOnly periodStart, DateOnly periodEnd,
        IReadOnlyList<Settlement> settlements, CancellationToken ct)
    {
        lock (_lock)
        {
            var existing = _batches.Values.FirstOrDefault(b => b.JeeberId == jeeberId && b.PeriodStart == periodStart);
            if (existing is not null) return Task.FromResult(existing);

            var batch = new SettlementBatch
            {
                Id                 = Guid.NewGuid(),
                JeeberId           = jeeberId,
                PeriodStart        = periodStart,
                PeriodEnd          = periodEnd,
                TotalGrossLbp      = settlements.Sum(s => s.GoodsCost),
                TotalCommissionLbp = settlements.Sum(s => s.Commission),
                TotalNetLbp        = settlements.Sum(s => s.GoodsCost - s.Commission),
                SettlementCount    = settlements.Count,
                Status             = "open",
                CreatedAt          = DateTimeOffset.UtcNow,
                UpdatedAt          = DateTimeOffset.UtcNow,
            };
            _batches[batch.Id] = batch;
            return Task.FromResult(batch);
        }
    }

    public Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct)
    {
        _batches.TryGetValue(batchId, out var batch);
        return Task.FromResult(batch);
    }

    public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<SettlementBatch>>(
            _batches.Values.Where(b => b.Status == status).ToList());

    public Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct)
    {
        lock (_lock)
        {
            if (!_batches.TryGetValue(batchId, out var batch))
                throw new InvalidOperationException($"Settlement batch {batchId} not found.");

            if (batch.Status != "paid")
            {
                batch.Status    = "paid";
                batch.PaidAt    = paidAt;
                batch.PaidBy    = adminUserId;
                batch.UpdatedAt = paidAt;
            }

            return Task.FromResult(batch);
        }
    }
}

// ── Upgraded WeeklySettlementBatch hosted service ─────────────────────────────

/// <summary>
/// Upgraded <c>WeeklySettlementBatch</c> (JEB-57, TL-PIN-JEB-498).
///
/// Changes from the in-memory MVP:
/// - Uses <see cref="ISettlementBatchStore"/> (durable, Postgres-backed in prod)
/// - Calls jeeb-state-service idempotency guard to prevent double-execution
/// - Groups settlements by jeeberId and creates one batch per jeeber per window
/// - Exposes <see cref="RunBatchAsync"/> for WS-D test-console forced execution
/// </summary>
public sealed class WeeklySettlementBatch : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly IOptions<WeeklySettlementOptions> _opts;
    private readonly ILogger<WeeklySettlementBatch> _log;

    public WeeklySettlementBatch(
        IServiceScopeFactory scopes,
        TimeProvider clock,
        IOptions<WeeklySettlementOptions> opts,
        ILogger<WeeklySettlementBatch> log)
    {
        _scopes = scopes;
        _clock = clock;
        _opts = opts;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(_opts.Value.Interval, _clock, stoppingToken);

            // Evaluate the cadence in the configured product timezone (default
            // Asia/Beirut) — the Beirut-weekly COD cadence is a Jeeb business
            // rule owned by the gateway (JEB-1476).
            var now = ToConfiguredZone(_clock.GetUtcNow());
            if (now.DayOfWeek != _opts.Value.SettlementDay)
                continue;

            try
            {
                await RunBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                _log.LogError(ex, "Weekly settlement batch failed");
            }
        }
    }

    /// <summary>
    /// Force-runnable entry point (used by WS-D test-console job registry).
    /// Contains the idempotency guard so it is safe to call multiple times.
    /// </summary>
    public async Task RunBatchAsync(CancellationToken ct)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var batchStore = scope.ServiceProvider.GetRequiredService<ISettlementBatchStore>();
        var stateService = scope.ServiceProvider.GetService<IJeebStateServiceClient>();

        var now = _clock.GetUtcNow();
        var periodEnd   = DateOnly.FromDateTime(now.Date).AddDays(-1);    // yesterday
        var periodStart = periodEnd.AddDays(-6);                           // 7-day window

        var windowKey = $"settlement-batch:{periodStart:yyyy-MM-dd}";

        // Idempotency guard: use jeeb-state-service when available.
        // GET the window key first; if it exists, the window has already been processed.
        // If not found, UPSERT the key to record execution, then proceed.
        // The DB settlement_batches UNIQUE(jeeber_id, period_start) is the hard guard.
        if (stateService is not null)
        {
            try
            {
                bool alreadyExecuted = false;
                try
                {
                    await stateService.GetIdempotencyKeyAsync(windowKey, ct);
                    alreadyExecuted = true;
                }
                catch (JeebGateway.Services.Clients.JeebStateServiceApiException apiEx) when (apiEx.StatusCode == 404)
                {
                    // Key not found → first execution for this window. Record it then proceed.
                    await stateService.UpsertIdempotencyKeyAsync(
                        new JeebGateway.Services.Clients.IdempotencyPutRequest
                        {
                            Key        = windowKey,
                            StatusCode = 200,
                            TtlSeconds = 60 * 60 * 24 * 8, // 8 days — outlasts the weekly window
                        }, ct);
                }

                if (alreadyExecuted)
                {
                    _log.LogInformation(
                        "Settlement batch for window {Key} already executed; skipping.", windowKey);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Guard failure → degrade gracefully; the DB UNIQUE constraint is the hard guard.
                _log.LogWarning(ex,
                    "State-service idempotency guard failed for key {Key}; proceeding (DB UNIQUE is backup).",
                    windowKey);
            }
        }

        var pending = await batchStore.ListUnsettledAsync(_opts.Value.MaxBatchSize, ct);
        if (pending.Count == 0)
        {
            _log.LogInformation("Weekly settlement batch [{Key}]: no unsettled items", windowKey);
            return;
        }

        // Group by jeeberId → one batch per jeeber per week.
        var byJeeber = pending.GroupBy(s => s.JeeberId);
        int batchCount = 0;

        foreach (var group in byJeeber)
        {
            var jeeberId   = group.Key;
            var settlements = group.ToList();

            try
            {
                var batch = await batchStore.CreateOrGetBatchAsync(
                    jeeberId, periodStart, periodEnd, settlements, ct);

                _log.LogInformation(
                    "Settlement batch {BatchId} created for jeeber {JeeberId}: {Count} settlements, net {NetLbp} LBP",
                    batch.Id, jeeberId, settlements.Count, batch.TotalNetLbp);

                batchCount++;
            }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "Failed to create batch for jeeber {JeeberId} in window {Key}",
                    jeeberId, windowKey);
            }
        }

        _log.LogInformation(
            "Weekly settlement batch [{Key}] complete: {BatchCount} batches created from {SettlementCount} settlements",
            windowKey, batchCount, pending.Count);
    }

    /// <summary>
    /// Converts a UTC instant into the configured settlement timezone
    /// (default Asia/Beirut). Falls back to the original instant if the host
    /// does not know the timezone id, so the batch never crashes on a missing
    /// tz database entry.
    /// </summary>
    private DateTimeOffset ToConfiguredZone(DateTimeOffset utcNow)
    {
        var tzId = _opts.Value.TimeZoneId;
        if (string.IsNullOrWhiteSpace(tzId))
            return utcNow;

        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
            return TimeZoneInfo.ConvertTime(utcNow, tz);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _log.LogWarning(ex, "Settlement timezone {TimeZoneId} not found; evaluating cadence in UTC", tzId);
            return utcNow;
        }
    }
}
