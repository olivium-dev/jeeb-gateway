using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Users.DataExport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.Users.DataExport;

/// <summary>
/// T-backend-042 durability hardening — <see cref="PostgresDataExportStore"/> and
/// <see cref="DataExportWorker"/>.
///
/// OPT-IN real-wire suite, mirroring the <c>PostgresAdminEscalationStoreTests</c>
/// "RealWire" pattern: runs only when <c>JEEB_GW_PG_LIVE=1</c> AND
/// <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a reachable Postgres connection
/// string, so CI without a live gateway-Postgres instance stays green. Deliberately
/// carries NO connection string / credential default of any kind — both env vars
/// must be supplied by the runner. The suite applies the same idempotent DDL as
/// <c>db/migrations/0023_init_data_exports.sql</c> before exercising the store, so a
/// bare test database is sufficient (no separate migration-apply step required), and
/// cleans up every row it wrote.
/// </summary>
public class PostgresDataExportStoreTests
{
    [Fact]
    public async Task RequestAsync_SecondCallWhileOpen_ReturnsSameRow_NeverDoubleQueues()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();

        try
        {
            var first = await store.RequestAsync(userId, DataExportFormat.Json, default);
            var second = await store.RequestAsync(userId, DataExportFormat.Json, default);

            second.Id.Should().Be(first.Id, "an open export must not be double-queued");
            second.RequestedAt.Should().Be(first.RequestedAt);
            second.DueBy.Should().Be(first.DueBy);
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task ClaimNextAsync_ClaimsOldestQueued_AndSetsProcessing()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userA = "user-" + Guid.NewGuid();
        var userB = "user-" + Guid.NewGuid();

        try
        {
            var older = await store.RequestAsync(userA, DataExportFormat.Json, default);
            await Task.Delay(5); // ensure distinct requested_at ordering
            await store.RequestAsync(userB, DataExportFormat.Json, default);

            var claimed = await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);

            claimed.Should().NotBeNull();
            claimed!.Id.Should().Be(older.Id, "ClaimNextAsync must claim the oldest queued row first");
            claimed.Status.Should().Be(DataExportStatus.Processing);
            claimed.StartedAt.Should().NotBeNull();
        }
        finally
        {
            await CleanupAsync(conn, userA);
            await CleanupAsync(conn, userB);
        }
    }

    [Fact]
    public async Task MarkReadyAsync_ThenDownloadToken_RoundTrips_AndIsSingleUseOnDelivery()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();

        try
        {
            var queued = await store.RequestAsync(userId, DataExportFormat.Json, default);
            var claimed = await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
            claimed!.Id.Should().Be(queued.Id);

            var payload = System.Text.Encoding.UTF8.GetBytes("{\"profile\":{}}");
            var now = DateTimeOffset.UtcNow;
            var token = await store.MarkReadyAsync(
                claimed.Id, payload, "application/json", now, TimeSpan.FromDays(7), default);

            token.Should().NotBeNullOrEmpty();

            // Valid token round-trips the payload while ready.
            var fetched = await store.GetByDownloadTokenAsync(token, now, default);
            fetched.Should().NotBeNull();
            fetched!.Payload.Should().BeEquivalentTo(payload);
            fetched.Status.Should().Be(DataExportStatus.Ready);

            // First delivery succeeds...
            var delivered = await store.MarkDeliveredAsync(claimed.Id, now, default);
            delivered.Should().BeTrue();

            // ...a second delivery attempt against the same id fails (single-use)...
            var deliveredAgain = await store.MarkDeliveredAsync(claimed.Id, now, default);
            deliveredAgain.Should().BeFalse("the download token is single-use");

            // ...and the token no longer resolves at all once delivered.
            var afterDelivery = await store.GetByDownloadTokenAsync(token, now, default);
            afterDelivery.Should().BeNull("a delivered export's token must no longer resolve");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task MarkReadyAsync_ThrowsInvalidOperation_WhenRowNotProcessing()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();

        try
        {
            // Still `queued` — never claimed — so MarkReadyAsync must reject it,
            // matching InMemoryDataExportStore's exception contract.
            var queued = await store.RequestAsync(userId, DataExportFormat.Json, default);

            var act = async () => await store.MarkReadyAsync(
                queued.Id, new byte[] { 1 }, "application/json", DateTimeOffset.UtcNow, TimeSpan.FromDays(7), default);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*'{queued.Id}'*'queued'*");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task GetByDownloadTokenAsync_ReturnsNull_WhenLinkExpired()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();

        try
        {
            await store.RequestAsync(userId, DataExportFormat.Json, default);
            var claimed = await store.ClaimNextAsync(DateTimeOffset.UtcNow, default);
            var now = DateTimeOffset.UtcNow;
            var token = await store.MarkReadyAsync(
                claimed!.Id, new byte[] { 1, 2, 3 }, "application/json", now, TimeSpan.FromMinutes(1), default);

            // Ask again from a vantage point after the 1-minute link validity window.
            var afterExpiry = now.AddMinutes(2);
            var result = await store.GetByDownloadTokenAsync(token, afterExpiry, default);

            result.Should().BeNull("the download link's validity window has elapsed");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task ListOverdueOpenAsync_ReturnsOnlyBreachedRows_AndWorkerSweepMarksThemFailed()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var overdueUser = "user-" + Guid.NewGuid();
        var freshUser = "user-" + Guid.NewGuid();

        try
        {
            var overdue = await store.RequestAsync(overdueUser, DataExportFormat.Json, default);

            // Insert the "still within SLA" row directly with a dueBy well beyond the
            // default 72h Sla, rather than via a second RequestAsync — both calls
            // share the same fixed 72h Sla, so a second RequestAsync's dueBy would
            // land only milliseconds after `overdue`'s, not safely outside the sweep
            // window below (which sits at ~72h + 1s).
            await using (var insertFresh = new NpgsqlCommand(
                """
                INSERT INTO data_exports (id, user_id, status, format, requested_at, due_by)
                VALUES (@Id, @UserId, 'queued', 'json', now(), now() + interval '100 hours')
                """, conn))
            {
                insertFresh.Parameters.AddWithValue("Id", Guid.NewGuid());
                insertFresh.Parameters.AddWithValue("UserId", freshUser);
                await insertFresh.ExecuteNonQueryAsync();
            }

            // "now" for the sweep is just past the overdue row's dueBy (~72h out) —
            // the fresh row's dueBy (100h out) is nowhere near breached.
            var sweepNow = overdue.DueBy.AddSeconds(1);

            var overdueRows = await store.ListOverdueOpenAsync(sweepNow, default);
            overdueRows.Should().ContainSingle(r => r.Id == overdue.Id);

            var services = new ServiceCollection();
            services.AddSingleton(store);
            await using var provider = services.BuildServiceProvider();

            var worker = new DataExportWorker(
                provider,
                new FixedTimeProvider(sweepNow),
                Options.Create(new DataExportOptions()),
                NullLogger<DataExportWorker>.Instance);

            // >= rather than == : this is a whole-table sweep, so a shared live test
            // database could carry unrelated leftover overdue rows from an earlier
            // interrupted run (same robustness caveat PostgresAdminEscalationStoreTests
            // takes for its whole-table ListAsync assertions). The row-specific
            // assertions below are the precise check.
            var sweptCount = await worker.SweepOnceAsync(default);
            sweptCount.Should().BeGreaterThanOrEqualTo(1);

            var afterSweep = await store.GetLatestForUserAsync(overdueUser, default);
            afterSweep!.Status.Should().Be(DataExportStatus.Failed, "the 72h SLA was breached");
            afterSweep.FailureReason.Should().Contain("SLA");

            var freshUnaffected = await store.GetLatestForUserAsync(freshUser, default);
            freshUnaffected!.Status.Should().Be(DataExportStatus.Queued, "this row is not yet overdue");
        }
        finally
        {
            await CleanupAsync(conn, overdueUser);
            await CleanupAsync(conn, freshUser);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task CleanupAsync(NpgsqlConnection conn, string userId)
    {
        await using var cmd = new NpgsqlCommand("DELETE FROM data_exports WHERE user_id = @UserId", conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No connection
    /// string ever lives in source.
    /// </summary>
    private static async Task<(PostgresDataExportStore Store, NpgsqlConnection Conn)?> TryCreateStoreAsync()
    {
        if (Environment.GetEnvironmentVariable("JEEB_GW_PG_LIVE") != "1")
        {
            return null;
        }

        var connectionString = Environment.GetEnvironmentVariable("JEEB_GW_PG_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return null;
        }

        var factory = new NpgsqlConnectionFactory(connectionString);
        var conn = await factory.OpenAsync(default);
        await EnsureSchemaAsync(conn);

        var store = new PostgresDataExportStore(
            factory,
            TimeProvider.System,
            Options.Create(new DataExportOptions()),
            NullLoggerFor<PostgresDataExportStore>());
        return (store, conn);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0023_init_data_exports.sql (table +
    /// indexes; schema_migrations bookkeeping is left to the real migration runner)
    /// so this suite works against a bare test database.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";
            CREATE TABLE IF NOT EXISTS data_exports (
                id                   UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id              TEXT        NOT NULL,
                status               TEXT        NOT NULL DEFAULT 'queued',
                format               TEXT        NOT NULL DEFAULT 'json',
                requested_at         TIMESTAMPTZ NOT NULL DEFAULT now(),
                due_by               TIMESTAMPTZ NOT NULL,
                started_at           TIMESTAMPTZ NULL,
                completed_at         TIMESTAMPTZ NULL,
                delivered_at         TIMESTAMPTZ NULL,
                failed_at            TIMESTAMPTZ NULL,
                failure_reason       TEXT        NULL,
                download_token       TEXT        NULL,
                token_used           BOOLEAN     NOT NULL DEFAULT FALSE,
                expires_at           TIMESTAMPTZ NULL,
                payload              BYTEA       NULL,
                payload_content_type TEXT        NULL,
                payload_size_bytes   BIGINT      NULL,
                created_at           TIMESTAMPTZ NOT NULL DEFAULT now(),
                updated_at           TIMESTAMPTZ NOT NULL DEFAULT now()
            );
            CREATE UNIQUE INDEX IF NOT EXISTS uq_data_exports_user_open
                ON data_exports (user_id)
                WHERE status IN ('queued', 'processing', 'ready');
            CREATE INDEX IF NOT EXISTS idx_data_exports_user_requested
                ON data_exports (user_id, requested_at DESC);
            CREATE INDEX IF NOT EXISTS idx_data_exports_queued_requested
                ON data_exports (requested_at)
                WHERE status = 'queued';
            CREATE UNIQUE INDEX IF NOT EXISTS uq_data_exports_download_token
                ON data_exports (download_token)
                WHERE download_token IS NOT NULL;
            CREATE INDEX IF NOT EXISTS idx_data_exports_open_due_by
                ON data_exports (due_by)
                WHERE status IN ('queued', 'processing');
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    /// <summary>Minimal manually-set TimeProvider so the SLA sweep test controls "now" precisely.</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
