using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.ProhibitedItems;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// jeeb-gateway durability hardening — <see cref="PostgresProhibitedItemsStore"/>.
///
/// OPT-IN real-wire suite, mirroring the <c>PostgresAdminEscalationStoreTests</c> /
/// <c>BanServiceContractTests</c> "RealWire" pattern: runs only when
/// <c>JEEB_GW_PG_LIVE=1</c> AND <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a
/// reachable Postgres connection string, so CI without a live gateway-Postgres
/// instance stays green. Deliberately carries NO connection string / credential
/// default of any kind — both env vars must be supplied by the runner. The suite
/// applies the same idempotent DDL as <c>db/migrations/0018_init_prohibited_item_acks.sql</c>
/// (plus the minimal <c>prohibited_items</c> shape from 0005 it ALTERs) before
/// exercising the store, so a bare test database is sufficient. Every row written
/// is cleaned up in a <c>finally</c> block.
///
/// NOTE: the local <see cref="EnsureSchemaAsync"/> replica of <c>prohibited_items</c>
/// intentionally omits the <c>created_by</c> / <c>updated_by</c> FK onto
/// <c>users(id)</c> that the real migration 0005 has — this suite tests
/// <see cref="PostgresProhibitedItemsStore"/>'s own logic, not that pre-existing,
/// unrelated referential-integrity constraint, and a bare test database has no
/// <c>users</c> table to satisfy it against.
/// </summary>
public class PostgresProhibitedItemsStoreTests
{
    [Fact]
    public async Task CreateAsync_PersistsSeverityAndRoundTripsThroughGetAsync()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var name = "test-item-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(new ProhibitedItemCreate
            {
                Name = name,
                Category = "weapons",
                Description = "integration-test row",
                Severity = ProhibitedSeverity.Warn
            }, adminUserId: Guid.NewGuid().ToString(), default);

            created.Name.Should().Be(name);
            created.Severity.Should().Be(ProhibitedSeverity.Warn);
            created.Active.Should().BeTrue();

            var fetched = await store.GetAsync(created.Id, default);
            fetched.Should().NotBeNull();
            fetched!.Name.Should().Be(name);
            fetched.Category.Should().Be("weapons");
            fetched.Severity.Should().Be(ProhibitedSeverity.Warn);
            fetched.CreatedBy.Should().NotBeNull();
        }
        finally
        {
            await CleanupItemAsync(conn, name);
        }
    }

    [Fact]
    public async Task CreateAsync_NonUuidAdminId_PersistsWithNullCreatedBy()
    {
        // DefaultLexiconSeeder calls CreateAsync with the literal sentinel
        // "system:lexicon-seed" as adminUserId — not a UUID. The store must
        // degrade to NULL for created_by/updated_by rather than throwing and
        // taking the whole seed/write down (see PostgresProhibitedItemsStore's
        // ParseUserIdOrNull remarks).
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var name = "test-item-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(
                new ProhibitedItemCreate { Name = name, Category = "alcohol" },
                adminUserId: "system:lexicon-seed",
                default);

            created.CreatedBy.Should().BeNull();
            created.UpdatedBy.Should().BeNull();
            created.Severity.Should().Be(ProhibitedSeverity.Block, "Severity defaults to Block when omitted");
        }
        finally
        {
            await CleanupItemAsync(conn, name);
        }
    }

    [Fact]
    public async Task CreateAsync_DuplicateNameCaseInsensitive_ThrowsDuplicateException()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var name = "Duplicate-Test-" + Guid.NewGuid();

        try
        {
            await store.CreateAsync(
                new ProhibitedItemCreate { Name = name, Category = "weapons" }, "admin-1", default);

            var act = async () => await store.CreateAsync(
                new ProhibitedItemCreate { Name = name.ToUpperInvariant(), Category = "drugs" }, "admin-1", default);

            await act.Should().ThrowAsync<DuplicateProhibitedItemNameException>();
        }
        finally
        {
            await CleanupItemAsync(conn, name);
        }
    }

    [Fact]
    public async Task UpdateAsync_PartialPatch_OnlyChangesGivenFields()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var name = "test-item-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(new ProhibitedItemCreate
            {
                Name = name,
                Category = "weapons",
                Description = "original",
                Severity = ProhibitedSeverity.Block
            }, "admin-1", default);

            // Only flip Active — Name/Category/Description/Severity must survive untouched.
            var updated = await store.UpdateAsync(created.Id, new ProhibitedItemPatch { Active = false }, "admin-2", default);

            updated.Should().NotBeNull();
            updated!.Active.Should().BeFalse();
            updated.Name.Should().Be(name);
            updated.Category.Should().Be("weapons");
            updated.Description.Should().Be("original");
            updated.Severity.Should().Be(ProhibitedSeverity.Block);
            updated.UpdatedBy.Should().NotBeNull();
        }
        finally
        {
            await CleanupItemAsync(conn, name);
        }
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        var result = await store.UpdateAsync(Guid.NewGuid().ToString(), new ProhibitedItemPatch { Active = false }, "admin-1", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_MalformedId_ReturnsNullInsteadOfThrowing()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        var result = await store.GetAsync("not-a-guid", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListActiveAsync_ExcludesInactiveRows()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var activeName = "active-" + Guid.NewGuid();
        var inactiveName = "inactive-" + Guid.NewGuid();

        try
        {
            var active = await store.CreateAsync(
                new ProhibitedItemCreate { Name = activeName, Category = "weapons" }, "admin-1", default);
            var inactive = await store.CreateAsync(
                new ProhibitedItemCreate { Name = inactiveName, Category = "weapons" }, "admin-1", default);
            await store.UpdateAsync(inactive.Id, new ProhibitedItemPatch { Active = false }, "admin-1", default);

            var activeList = await store.ListActiveAsync(default);

            activeList.Should().Contain(i => i.Id == active.Id);
            activeList.Should().NotContain(i => i.Id == inactive.Id);
        }
        finally
        {
            await CleanupItemAsync(conn, activeName);
            await CleanupItemAsync(conn, inactiveName);
        }
    }

    [Fact]
    public async Task AcknowledgeAsync_ThenGetAcknowledgmentAsync_RoundTrips()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();
        var version = DateTimeOffset.UtcNow.ToString("O");

        try
        {
            var ack = await store.AcknowledgeAsync(userId, version, default);
            ack.UserId.Should().Be(userId);
            ack.Version.Should().Be(version);

            var fetched = await store.GetAcknowledgmentAsync(userId, default);
            fetched.Should().NotBeNull();
            fetched!.Version.Should().Be(version);
        }
        finally
        {
            await CleanupAckAsync(conn, userId);
        }
    }

    [Fact]
    public async Task AcknowledgeAsync_SameVersionTwice_IsIdempotentNotDuplicate()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();
        var version = "empty";

        try
        {
            await store.AcknowledgeAsync(userId, version, default);
            // Double-tap / client retry on the SAME version must not throw and must
            // not create a second row (PRIMARY KEY (user_id, lexicon_version) upsert).
            var second = await store.AcknowledgeAsync(userId, version, default);
            second.Version.Should().Be(version);

            await using var countCmd = new NpgsqlCommand(
                "SELECT COUNT(*) FROM prohibited_item_acks WHERE user_id = @UserId", conn);
            countCmd.Parameters.AddWithValue("UserId", userId);
            var count = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
            count.Should().Be(1);
        }
        finally
        {
            await CleanupAckAsync(conn, userId);
        }
    }

    [Fact]
    public async Task AcknowledgeAsync_NewerVersion_GetAcknowledgmentAsyncReturnsNewest()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "user-" + Guid.NewGuid();
        var v1 = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");
        var v2 = DateTimeOffset.UtcNow.ToString("O");

        try
        {
            await store.AcknowledgeAsync(userId, v1, default);
            await Task.Delay(5); // distinct acknowledged_at tick
            await store.AcknowledgeAsync(userId, v2, default);

            var latest = await store.GetAcknowledgmentAsync(userId, default);
            latest.Should().NotBeNull();
            latest!.Version.Should().Be(v2, "GetAcknowledgmentAsync resolves the newest ack, matching in-memory's last-write-wins read");
        }
        finally
        {
            await CleanupAckAsync(conn, userId);
        }
    }

    [Fact]
    public async Task GetAcknowledgmentAsync_NoRows_ReturnsNull()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        var result = await store.GetAcknowledgmentAsync("no-such-user-" + Guid.NewGuid(), default);
        result.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task CleanupItemAsync(NpgsqlConnection conn, string name)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM prohibited_items WHERE LOWER(name) = LOWER(@Name)", conn);
        cmd.Parameters.AddWithValue("Name", name);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAckAsync(NpgsqlConnection conn, string userId)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM prohibited_item_acks WHERE user_id = @UserId", conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No connection
    /// string ever lives in source.
    /// </summary>
    private static async Task<(PostgresProhibitedItemsStore Store, NpgsqlConnection Conn)?> TryCreateStoreAsync()
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

        var store = new PostgresProhibitedItemsStore(factory, NullLoggerFor<PostgresProhibitedItemsStore>());
        return (store, conn);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0005 (prohibited_items, minimal
    /// shape) + 0018 (severity ALTER, prohibited_item_acks) so this suite works
    /// against a bare test database with no separate migration-apply step.
    /// Deliberately omits the created_by/updated_by REFERENCES users(id) FK —
    /// see class remarks.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";

            CREATE TABLE IF NOT EXISTS prohibited_items (
                id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                name         TEXT         NOT NULL,
                category     TEXT         NOT NULL,
                description  TEXT         NULL,
                active       BOOLEAN      NOT NULL DEFAULT TRUE,
                created_by   UUID         NULL,
                updated_by   UUID         NULL,
                created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW(),
                updated_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );

            CREATE UNIQUE INDEX IF NOT EXISTS prohibited_items_name_lower_uniq
                ON prohibited_items (LOWER(name));

            ALTER TABLE prohibited_items
                ADD COLUMN IF NOT EXISTS severity TEXT NOT NULL DEFAULT 'block';

            CREATE TABLE IF NOT EXISTS prohibited_item_acks (
                user_id         TEXT        NOT NULL,
                lexicon_version TEXT        NOT NULL,
                acknowledged_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                PRIMARY KEY (user_id, lexicon_version)
            );

            CREATE INDEX IF NOT EXISTS prohibited_item_acks_user_latest_idx
                ON prohibited_item_acks (user_id, acknowledged_at DESC);
            """, conn);
        await cmd.ExecuteNonQueryAsync();

        await using var constraintCmd = new NpgsqlCommand(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1 FROM pg_constraint WHERE conname = 'prohibited_items_severity_format'
                ) THEN
                    ALTER TABLE prohibited_items
                        ADD CONSTRAINT prohibited_items_severity_format
                        CHECK (severity IN ('warn', 'block'));
                END IF;
            END$$;
            """, conn);
        await constraintCmd.ExecuteNonQueryAsync();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
