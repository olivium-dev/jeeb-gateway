using FluentAssertions;
using JeebGateway.Infrastructure;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// Settlement-shape convergence regression suite (SETTLEMENT-SHAPE-FINDING.md).
///
/// Locks the single canonical cash-USD <c>settlement_batches</c> shape the gateway
/// app reads/writes (<see cref="JeebGateway.Financials.PostgresSettlementBatchStore"/>
/// in Financials/WeeklySettlementBatch.cs) by exercising the REAL migration files on
/// disk — <c>db/migrations/0037_converge_settlement_batches_usd.sql</c> (the converger)
/// and <c>db/migrations/0038_assert_settlement_batches_canonical_usd.sql</c> (the guard).
///
/// The disease this guards against: <c>settlement_batches</c> is CREATEd by BOTH 0008
/// (retired UPG payout shape) and 0015 (cash <c>_lbp</c> shape) under
/// <c>CREATE TABLE IF NOT EXISTS</c>, so a fresh DB silently lands the WRONG shape and
/// the live dev DB physically carried the <c>_lbp</c> shape. 0037 converges every
/// starting state to canonical; 0038 fails the migration run loudly if it ever drifts.
///
/// OPT-IN "RealWire" suite (mirrors the repo's PostgresAdminEscalationStoreTests
/// pattern): runs only when <c>JEEB_GW_PG_LIVE=1</c> AND <c>JEEB_GW_PG_TEST_CONNECTION</c>
/// point at a reachable, DISPOSABLE test Postgres, so CI without a live gateway-Postgres
/// stays green. Carries NO connection string / credential default. This suite OWNS the
/// <c>settlement_batches</c> / <c>settlements</c> tables in the test DB: it drops and
/// recreates them per test and drops them in a finally.
/// </summary>
public class SettlementBatchesCanonicalShapeTests
{
    // The 14 canonical cash-USD columns the app requires.
    private static readonly string[] CanonicalColumns =
    {
        "id", "jeeber_id", "period_start", "period_end",
        "total_gross_usd", "total_commission_usd", "total_net_usd",
        "settlement_count", "currency", "status",
        "paid_at", "paid_by", "created_at", "updated_at"
    };

    [Fact]
    public async Task StuckLbpShape_Converges_ToCanonicalUsd_AndGuardPasses()
    {
        var conn = await TryOpenAsync();
        if (conn is null) return; // opt-in only; not run in CI
        await using var _ = conn;

        try
        {
            await ResetAsync(conn);

            // Reproduce the LIVE stuck cash-era _lbp shape (db/migrations/0015 verbatim).
            await ExecAsync(conn, LbpSettlementBatchesDdl);
            await ExecAsync(conn, LbpSettlementsDdl);
            await ExecAsync(conn,
                """
                INSERT INTO settlement_batches
                  (jeeber_id, period_start, period_end, total_gross_lbp, total_commission_lbp, total_net_lbp, settlement_count, currency, status)
                VALUES ('jeeber-x', DATE '2026-06-01', DATE '2026-06-07', 1500000.0000, 150000.0000, 1350000.0000, 3, 'LBP', 'open');
                """);
            // Mirror the ledger drift: live records only 0008 for the settlement chain.
            await ExecAsync(conn, "INSERT INTO schema_migrations (version) VALUES ('0008_init_financial_ledger') ON CONFLICT DO NOTHING;");

            // Run the REAL converger.
            await ExecFileAsync(conn, MigrationPath("0037_converge_settlement_batches_usd.sql"));

            await AssertCanonicalShapeAsync(conn);

            // Data preserved: row kept, magnitude unchanged (no FX), currency relabeled USD.
            (await ScalarLongAsync(conn, "SELECT count(*) FROM settlement_batches"))
                .Should().Be(1, "the _lbp rename is non-destructive");
            (await ScalarStringAsync(conn, "SELECT currency FROM settlement_batches WHERE jeeber_id = 'jeeber-x'"))
                .Should().Be("USD", "0037 relabels LBP -> USD without FX conversion");
            (await ScalarStringAsync(conn, "SELECT total_gross_usd::text FROM settlement_batches WHERE jeeber_id = 'jeeber-x'"))
                .Should().Be("1500000.0000", "the numeric magnitude must be preserved under the renamed column");

            // The guard migration must PASS on the converged shape.
            await ExecFileAsync(conn, MigrationPath("0038_assert_settlement_batches_canonical_usd.sql"));
        }
        finally
        {
            await ResetAsync(conn);
        }
    }

    [Fact]
    public async Task RetiredUpgShape_Converges_ToCanonicalUsd_AndGuardPasses()
    {
        var conn = await TryOpenAsync();
        if (conn is null) return; // opt-in only; not run in CI
        await using var _ = conn;

        try
        {
            await ResetAsync(conn);

            // Reproduce the retired-UPG shape (what 0008 lands on a fresh DB), EMPTY.
            // payout_method/status kept as TEXT so the test needs no enum types; 0037's
            // marker is the total_payout column and its empty-table reshape path.
            await ExecAsync(conn,
                """
                CREATE TABLE settlement_batches (
                    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
                    jeeber_id           UUID NOT NULL,
                    period_start        DATE NOT NULL,
                    period_end          DATE NOT NULL,
                    total_commission    NUMERIC(12,2) NOT NULL DEFAULT 0,
                    total_payout        NUMERIC(12,2) NOT NULL DEFAULT 0,
                    delivery_count      INTEGER NOT NULL DEFAULT 0,
                    payout_method       TEXT NOT NULL DEFAULT 'bank_transfer',
                    status              TEXT NOT NULL DEFAULT 'pending',
                    processed_at        TIMESTAMPTZ,
                    paid_at             TIMESTAMPTZ,
                    failed_at           TIMESTAMPTZ,
                    cancelled_at        TIMESTAMPTZ,
                    failure_reason      TEXT,
                    external_reference  TEXT,
                    created_at          TIMESTAMPTZ NOT NULL DEFAULT now(),
                    updated_at          TIMESTAMPTZ NOT NULL DEFAULT now()
                );
                """);

            await ExecFileAsync(conn, MigrationPath("0037_converge_settlement_batches_usd.sql"));

            await AssertCanonicalShapeAsync(conn);
            (await ColumnExistsAsync(conn, "total_payout")).Should().BeFalse("the retired UPG payout column must be dropped");
            (await ColumnExistsAsync(conn, "payout_method")).Should().BeFalse("the retired UPG payout_method column must be dropped");

            await ExecFileAsync(conn, MigrationPath("0038_assert_settlement_batches_canonical_usd.sql"));
        }
        finally
        {
            await ResetAsync(conn);
        }
    }

    [Fact]
    public async Task Guard0038_Throws_WhenStaleLbpColumnLeaksBack()
    {
        var conn = await TryOpenAsync();
        if (conn is null) return; // opt-in only; not run in CI
        await using var _ = conn;

        try
        {
            await ResetAsync(conn);

            // Start canonical (0037 step 0 creates the canonical shape from no table).
            await ExecFileAsync(conn, MigrationPath("0037_converge_settlement_batches_usd.sql"));
            await AssertCanonicalShapeAsync(conn);

            // Simulate a regression: a stale _lbp column leaks back onto the table.
            await ExecAsync(conn, "ALTER TABLE settlement_batches RENAME COLUMN total_gross_usd TO total_gross_lbp;");

            // The guard MUST fail the migration run (this is what protects prod deploys).
            Func<Task> act = () => ExecFileAsync(conn, MigrationPath("0038_assert_settlement_batches_canonical_usd.sql"));
            (await act.Should().ThrowAsync<PostgresException>())
                .Which.MessageText.Should().Contain("settlement-shape guard (0038)");
        }
        finally
        {
            await ResetAsync(conn);
        }
    }

    // ── canonical-shape assertions ────────────────────────────────────────────

    private static async Task AssertCanonicalShapeAsync(NpgsqlConnection conn)
    {
        foreach (var col in CanonicalColumns)
        {
            (await ColumnExistsAsync(conn, col))
                .Should().BeTrue($"canonical settlement_batches must expose '{col}'");
        }

        (await ScalarLongAsync(conn,
            "SELECT count(*) FROM information_schema.columns " +
            "WHERE table_schema='public' AND table_name='settlement_batches' AND column_name LIKE '%\\_lbp'"))
            .Should().Be(0, "no cash-era _lbp columns may remain after convergence");

        (await ScalarStringAsync(conn,
            "SELECT data_type FROM information_schema.columns " +
            "WHERE table_schema='public' AND table_name='settlement_batches' AND column_name='jeeber_id'"))
            .Should().Be("text", "the app reads jeeber_id via reader.GetString");
    }

    // ── DDL fragments (db/migrations/0015 verbatim, trimmed) ──────────────────

    private const string LbpSettlementBatchesDdl =
        """
        CREATE TABLE settlement_batches (
            id                   UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            jeeber_id            TEXT NOT NULL,
            period_start         DATE NOT NULL,
            period_end           DATE NOT NULL,
            total_gross_lbp      NUMERIC(20,4) NOT NULL DEFAULT 0,
            total_commission_lbp NUMERIC(20,4) NOT NULL DEFAULT 0,
            total_net_lbp        NUMERIC(20,4) NOT NULL DEFAULT 0,
            settlement_count     INT NOT NULL DEFAULT 0,
            currency             TEXT NOT NULL DEFAULT 'LBP',
            status               TEXT NOT NULL DEFAULT 'open',
            paid_at              TIMESTAMPTZ,
            paid_by              TEXT,
            created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            updated_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT uq_settlement_batches_jeeber_period UNIQUE (jeeber_id, period_start)
        );
        """;

    private const string LbpSettlementsDdl =
        """
        CREATE TABLE settlements (
            id            UUID PRIMARY KEY DEFAULT gen_random_uuid(),
            delivery_id   TEXT NOT NULL,
            jeeber_id     TEXT NOT NULL,
            currency      TEXT NOT NULL DEFAULT 'LBP',
            settled_at    TIMESTAMPTZ NOT NULL DEFAULT now(),
            CONSTRAINT uq_settlements_delivery_id UNIQUE (delivery_id)
        );
        """;

    // ── harness helpers ───────────────────────────────────────────────────────

    private static async Task ResetAsync(NpgsqlConnection conn)
    {
        // A guard migration that RAISEs inside its BEGIN;...COMMIT; leaves the
        // connection in an aborted transaction — clear it before cleanup DDL.
        try
        {
            await using var rb = new NpgsqlCommand("ROLLBACK;", conn);
            await rb.ExecuteNonQueryAsync();
        }
        catch { /* no transaction in progress — fine */ }

        await ExecAsync(conn,
            "DROP TABLE IF EXISTS settlements CASCADE; " +
            "DROP TABLE IF EXISTS settlement_batches CASCADE; " +
            "CREATE TABLE IF NOT EXISTS schema_migrations (version TEXT PRIMARY KEY);");
    }

    private static async Task<bool> ColumnExistsAsync(NpgsqlConnection conn, string column)
        => await ScalarLongAsync(conn,
            "SELECT count(*) FROM information_schema.columns " +
            $"WHERE table_schema='public' AND table_name='settlement_batches' AND column_name='{column}'") == 1;

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecFileAsync(NpgsqlConnection conn, string path)
        => await ExecAsync(conn, await File.ReadAllTextAsync(path));

    private static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarStringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var v = await cmd.ExecuteScalarAsync();
        return v is DBNull or null ? null : v.ToString();
    }

    /// <summary>Absolute path to a file under db/migrations, found by walking up from the test assembly.</summary>
    private static string MigrationPath(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "db", "migrations", fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate db/migrations/{fileName} by walking up from {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No connection
    /// string ever lives in source.
    /// </summary>
    private static async Task<NpgsqlConnection?> TryOpenAsync()
    {
        if (Environment.GetEnvironmentVariable("JEEB_GW_PG_LIVE") != "1")
            return null;

        var connectionString = Environment.GetEnvironmentVariable("JEEB_GW_PG_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        var factory = new NpgsqlConnectionFactory(connectionString);
        return await factory.OpenAsync(default);
    }
}
