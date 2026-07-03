using System.Text.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.Admin;

/// <summary>
/// T-backend-030 durability hardening — <see cref="PostgresAdminAuditLog"/>.
///
/// OPT-IN real-wire suite, mirroring the <c>PostgresAdminEscalationStoreTests</c>
/// / <c>BanServiceContractTests</c> "RealWire" pattern: runs only when
/// <c>JEEB_GW_PG_LIVE=1</c> AND <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a
/// reachable Postgres connection string, so CI without a live gateway-Postgres
/// instance stays green. Deliberately carries NO connection string / credential
/// default of any kind — both env vars must be supplied by the runner. The
/// suite applies the same idempotent DDL as
/// <c>db/migrations/0005_init_prohibited_items_admin_audit_notif_prefs.sql</c>'s
/// <c>admin_actions</c> table (plus a minimal <c>users</c> stand-in so the
/// NOT-NULL <c>admin_user_id</c> foreign key can be satisfied) before
/// exercising the store, so a bare test database is sufficient (no separate
/// migration-apply step required), and cleans up every row it wrote.
/// </summary>
public class PostgresAdminAuditLogTests
{
    [Fact]
    public async Task AppendAsync_PersistsRow_RoundTripsFieldsAndJsonbStates()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;
        var entityId = Guid.NewGuid().ToString();

        try
        {
            var created = await store.AppendAsync(new AdminAuditAppend
            {
                AdminUserId = adminUserId,
                Action = "resolve_dispute",
                EntityType = "dispute_case",
                EntityId = entityId,
                BeforeState = new Dictionary<string, object?> { ["status"] = "under_review" },
                AfterState = new Dictionary<string, object?> { ["status"] = "resolved" },
                RequestId = "req-abc-123",
            }, default);

            created.Id.Should().NotBeNullOrWhiteSpace();
            created.AdminUserId.Should().Be(adminUserId);
            created.Action.Should().Be("resolve_dispute");
            created.EntityType.Should().Be("dispute_case");
            created.EntityId.Should().Be(entityId);
            created.RequestId.Should().Be("req-abc-123");
            StringValue(created.BeforeState, "status").Should().Be("under_review");
            StringValue(created.AfterState, "status").Should().Be("resolved");

            var timeline = await store.ListForEntityAsync("dispute_case", entityId, default);
            timeline.Should().ContainSingle(e => e.Id == created.Id);
            var fetched = timeline.Single(e => e.Id == created.Id);
            StringValue(fetched.AfterState, "status").Should().Be("resolved");
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId);
        }
    }

    [Fact]
    public async Task AppendAsync_PersistsNullEntityIdAndStates_ForSystemLevelActions()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;

        try
        {
            var created = await store.AppendAsync(new AdminAuditAppend
            {
                AdminUserId = adminUserId,
                Action = "rotated_keys",
                EntityType = "system",
                EntityId = null,
                BeforeState = null,
                AfterState = null,
                RequestId = null,
            }, default);

            created.EntityId.Should().BeNull();
            created.BeforeState.Should().BeNull();
            created.AfterState.Should().BeNull();
            created.RequestId.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId: null);
        }
    }

    [Fact]
    public async Task AppendAsync_NonGuidEntityId_DoesNotThrow_AndPersistsWithNullEntityId()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;

        // DisputeService mints ids like "dsp_<hex>" — not GUID-shaped. The
        // UUID-typed entity_id column can't hold them; the row must still be
        // written (malformed-id guard), never throw, never drop the audit row.
        var nonGuidEntityId = "dsp_" + Guid.NewGuid().ToString("N");

        try
        {
            var created = await store.AppendAsync(new AdminAuditAppend
            {
                AdminUserId = adminUserId,
                Action = "file_dispute",
                EntityType = "dispute",
                EntityId = nonGuidEntityId,
                AfterState = new Dictionary<string, object?> { ["id"] = nonGuidEntityId },
            }, default);

            created.Should().NotBeNull();
            created.EntityId.Should().BeNull("entity_id is UUID-typed and cannot hold a non-GUID id");
            StringValue(created.AfterState, "id").Should().Be(nonGuidEntityId, "the caller's own after_state snapshot is untouched");
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId: null);
        }
    }

    [Fact]
    public async Task ListForEntityAsync_ReturnsEmpty_ForNonGuidEntityId()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;

        try
        {
            // Guards against Guid.Parse throwing when a caller queries by a
            // non-GUID entity id — must return an empty timeline, not crash.
            var result = await store.ListForEntityAsync("dispute", "not-a-guid", default);

            result.Should().BeEmpty();
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId: null);
        }
    }

    [Fact]
    public async Task ListForEntityAsync_ReturnsEmpty_WhenNoMatchingRowsExist()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;

        try
        {
            var result = await store.ListForEntityAsync("user", Guid.NewGuid().ToString(), default);

            result.Should().BeEmpty();
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId: null);
        }
    }

    [Fact]
    public async Task ListForEntityAsync_OrdersNewestFirst_AndScopesToEntityTypeAndId()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn, adminUserId) = ctx.Value;
        await using var _ = conn;
        var entityId = Guid.NewGuid().ToString();
        var otherEntityId = Guid.NewGuid().ToString();

        try
        {
            var first = await store.AppendAsync(NewAppend(adminUserId, "user", entityId, "suspend_user"), default);
            await Task.Delay(5); // distinct created_at ordering tick
            var second = await store.AppendAsync(NewAppend(adminUserId, "user", entityId, "unsuspend_user"), default);

            // Different entity id — must not leak into the first entity's timeline.
            await store.AppendAsync(NewAppend(adminUserId, "user", otherEntityId, "suspend_user"), default);

            // ORDER BY created_at DESC — newest (second) first.
            var timeline = await store.ListForEntityAsync("user", entityId, default);
            timeline.Select(e => e.Id).Should().Equal(second.Id, first.Id);
            timeline.Should().OnlyContain(e => e.EntityId == entityId);
        }
        finally
        {
            await CleanupAsync(conn, adminUserId, entityId);
            await CleanupAsync(conn, adminUserId, otherEntityId);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AdminAuditAppend NewAppend(string adminUserId, string entityType, string entityId, string action) =>
        new()
        {
            AdminUserId = adminUserId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
        };

    /// <summary>
    /// System.Text.Json deserializes untyped <c>object?</c> dictionary values
    /// as <see cref="JsonElement"/> rather than the caller's original CLR
    /// type — expected, standard behaviour (no custom converter is
    /// registered, matching every other JSONB store in this codebase). Tests
    /// unwrap through this helper rather than asserting on the boxed type.
    /// </summary>
    private static string? StringValue(IReadOnlyDictionary<string, object?>? dict, string key)
    {
        if (dict is null || !dict.TryGetValue(key, out var value) || value is null) return null;
        return value is JsonElement je ? je.GetString() : value.ToString();
    }

    private static async Task CleanupAsync(NpgsqlConnection conn, string adminUserId, string? entityId)
    {
        if (entityId is not null && Guid.TryParse(entityId, out var guid))
        {
            await using var byEntity = new NpgsqlCommand(
                "DELETE FROM admin_actions WHERE entity_id = @EntityId", conn);
            byEntity.Parameters.AddWithValue("EntityId", guid);
            await byEntity.ExecuteNonQueryAsync();
        }

        await using var byAdmin = new NpgsqlCommand(
            "DELETE FROM admin_actions WHERE admin_user_id = @AdminUserId", conn);
        byAdmin.Parameters.AddWithValue("AdminUserId", Guid.Parse(adminUserId));
        await byAdmin.ExecuteNonQueryAsync();

        await using var user = new NpgsqlCommand("DELETE FROM users WHERE id = @Id", conn);
        user.Parameters.AddWithValue("Id", Guid.Parse(adminUserId));
        await user.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No
    /// connection string ever lives in source. Also seeds a minimal
    /// <c>users</c> row so admin_actions.admin_user_id's NOT-NULL foreign key
    /// (migration 0005) can be satisfied — the id is returned so each test's
    /// AdminAuditAppend.AdminUserId can reference it.
    /// </summary>
    private static async Task<(PostgresAdminAuditLog Store, NpgsqlConnection Conn, string AdminUserId)?> TryCreateStoreAsync()
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

        var adminUserId = await SeedAdminUserAsync(conn);
        var store = new PostgresAdminAuditLog(factory, NullLoggerFor<PostgresAdminAuditLog>());
        return (store, conn, adminUserId);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0005's <c>admin_actions</c> table
    /// (schema_migrations bookkeeping is left to the real migration runner)
    /// so this suite works against a bare test database. Also creates a
    /// minimal <c>users</c> stand-in — if the real migration 0001 users table
    /// already exists on the target database, this is a harmless no-op and
    /// the real table's (stricter) constraints apply instead.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";

            CREATE TABLE IF NOT EXISTS users (
                id           UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                phone        VARCHAR(20)  NOT NULL,
                name         VARCHAR(255) NOT NULL,
                created_at   TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );

            CREATE TABLE IF NOT EXISTS admin_actions (
                id            UUID         PRIMARY KEY DEFAULT gen_random_uuid(),
                admin_user_id UUID         NOT NULL REFERENCES users (id) ON DELETE RESTRICT,
                action        TEXT         NOT NULL,
                entity_type   TEXT         NOT NULL,
                entity_id     UUID         NULL,
                before_state  JSONB        NULL,
                after_state   JSONB        NULL,
                ip_address    INET         NULL,
                user_agent    TEXT         NULL,
                request_id    TEXT         NULL,
                created_at    TIMESTAMPTZ  NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS admin_actions_admin_created_idx
                ON admin_actions (admin_user_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS admin_actions_entity_idx
                ON admin_actions (entity_type, entity_id, created_at DESC)
                WHERE entity_id IS NOT NULL;
            CREATE INDEX IF NOT EXISTS admin_actions_created_at_idx
                ON admin_actions (created_at DESC);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Inserts a throwaway users row so admin_actions.admin_user_id's FK can
    /// resolve. Phone is derived per-call so this is safe to run repeatedly
    /// against a database that already enforces users_phone_uniq.
    /// </summary>
    private static async Task<string> SeedAdminUserAsync(NpgsqlConnection conn)
    {
        var id = Guid.NewGuid();
        var phoneDigits = (Math.Abs(id.GetHashCode()) % 100_000_000).ToString("D8");

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users (id, phone, name) VALUES (@Id, @Phone, @Name)", conn);
        cmd.Parameters.AddWithValue("Id", id);
        cmd.Parameters.AddWithValue("Phone", "+1" + phoneDigits);
        cmd.Parameters.AddWithValue("Name", "Audit Test Admin");
        await cmd.ExecuteNonQueryAsync();

        return id.ToString();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
