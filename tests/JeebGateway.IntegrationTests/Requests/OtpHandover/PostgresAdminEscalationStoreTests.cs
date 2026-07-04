using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Requests.OtpHandover;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.Requests.OtpHandover;

/// <summary>
/// T-backend-015 / JEEB-33 durability hardening — <see cref="PostgresAdminEscalationStore"/>.
///
/// OPT-IN real-wire suite, mirroring the <c>BanServiceContractTests</c>
/// "RealWire" pattern: runs only when <c>JEEB_GW_PG_LIVE=1</c> AND
/// <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a reachable Postgres connection
/// string, so CI without a live gateway-Postgres instance stays green.
/// Deliberately carries NO connection string / credential default of any kind —
/// both env vars must be supplied by the runner. The suite applies the same
/// idempotent DDL as <c>db/migrations/0021_init_admin_escalations.sql</c>
/// before exercising the store, so a bare test database is sufficient (no
/// separate migration-apply step required), and cleans up every row it wrote.
/// </summary>
public class PostgresAdminEscalationStoreTests
{
    [Fact]
    public async Task CreateAsync_HasNoUniquenessInvariant_GetForDelivery_ResolvesNewest()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var deliveryId = "test-delivery-" + Guid.NewGuid();

        try
        {
            var first = await store.CreateAsync(
                NewEscalation(deliveryId, EscalationReason.OtpLocked, attempts: 3), default);

            // Ensure a distinct created_at ordering tick between the two rows.
            await Task.Delay(5);

            // No per-delivery uniqueness invariant (see PostgresAdminEscalationStore
            // class remarks) — a second row for the same (deliveryId, reason) pair
            // must be allowed to land, exactly like InMemoryAdminEscalationStore.
            var second = await store.CreateAsync(
                NewEscalation(deliveryId, EscalationReason.OtpLocked, attempts: 5), default);

            first.Id.Should().NotBe(second.Id);

            var latest = await store.GetForDeliveryAsync(deliveryId, EscalationReason.OtpLocked, default);
            latest.Should().NotBeNull();
            latest!.Id.Should().Be(second.Id, "GetForDeliveryAsync resolves the newest matching row");
            latest.OtpAttemptCount.Should().Be(5);
            latest.Status.Should().Be(EscalationStatus.Pending);

            var all = await store.ListAsync(default);
            var indexOfFirst = IndexOfId(all, first.Id);
            var indexOfSecond = IndexOfId(all, second.Id);
            indexOfFirst.Should().BeGreaterOrEqualTo(0);
            indexOfSecond.Should().BeGreaterOrEqualTo(0);
            indexOfSecond.Should().BeLessThan(indexOfFirst, "ListAsync is newest-first (full scan)");
        }
        finally
        {
            await CleanupAsync(conn, deliveryId);
        }
    }

    [Fact]
    public async Task CreateAsync_PersistsNullJeeberId_ForClientUnreachableEscalations()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var deliveryId = "test-delivery-" + Guid.NewGuid();

        try
        {
            var entry = new AdminEscalation
            {
                Id = Guid.NewGuid().ToString(),
                DeliveryId = deliveryId,
                ClientId = "client-" + Guid.NewGuid(),
                JeeberId = null, // not yet matched when the client-unreachable timer fires
                Reason = EscalationReason.ClientUnreachable,
                Status = EscalationStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                OtpAttemptCount = 0,
            };

            var created = await store.CreateAsync(entry, default);
            created.JeeberId.Should().BeNull();
            created.DeliveryId.Should().Be(deliveryId);

            var fetched = await store.GetForDeliveryAsync(deliveryId, EscalationReason.ClientUnreachable, default);
            fetched.Should().NotBeNull();
            fetched!.JeeberId.Should().BeNull();
            fetched.Reason.Should().Be(EscalationReason.ClientUnreachable);
        }
        finally
        {
            await CleanupAsync(conn, deliveryId);
        }
    }

    [Fact]
    public async Task GetForDeliveryAsync_ReturnsNull_WhenNoMatchingRowExists()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        var result = await store.GetForDeliveryAsync("no-such-delivery-" + Guid.NewGuid(), EscalationReason.OtpLocked, default);

        result.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AdminEscalation NewEscalation(string deliveryId, string reason, int attempts) => new()
    {
        Id = Guid.NewGuid().ToString(),
        DeliveryId = deliveryId,
        ClientId = "client-" + Guid.NewGuid(),
        JeeberId = "jeeber-" + Guid.NewGuid(),
        Reason = reason,
        Status = EscalationStatus.Pending,
        CreatedAt = DateTimeOffset.UtcNow,
        OtpAttemptCount = attempts,
    };

    private static int IndexOfId(IReadOnlyList<AdminEscalation> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, id, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static async Task CleanupAsync(NpgsqlConnection conn, string deliveryId)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM admin_escalations WHERE delivery_id = @DeliveryId", conn);
        cmd.Parameters.AddWithValue("DeliveryId", deliveryId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No
    /// connection string ever lives in source.
    /// </summary>
    private static async Task<(PostgresAdminEscalationStore Store, NpgsqlConnection Conn)?> TryCreateStoreAsync()
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

        var store = new PostgresAdminEscalationStore(factory, NullLoggerFor<PostgresAdminEscalationStore>());
        return (store, conn);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0021_init_admin_escalations.sql
    /// (table + delivery_id index; schema_migrations bookkeeping is left to the
    /// real migration runner) so this suite works against a bare test database.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";
            CREATE TABLE IF NOT EXISTS admin_escalations (
                id                 UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
                delivery_id        TEXT        NOT NULL,
                client_id          TEXT        NOT NULL,
                jeeber_id          TEXT        NULL,
                reason             TEXT        NOT NULL,
                status             TEXT        NOT NULL,
                otp_attempt_count  INT         NOT NULL DEFAULT 0,
                created_at         TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                updated_at         TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            CREATE INDEX IF NOT EXISTS idx_admin_escalations_delivery_id
                ON admin_escalations (delivery_id);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
