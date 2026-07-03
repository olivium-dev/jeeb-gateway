using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.Users.SavedLocations;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.Users.SavedLocations;

/// <summary>
/// ACCT-04 / REQ-02 durability hardening — <see cref="PostgresSavedLocationStore"/>.
///
/// OPT-IN real-wire suite, mirroring the
/// <c>PostgresAdminEscalationStoreTests</c> "RealWire" pattern: runs only when
/// <c>JEEB_GW_PG_LIVE=1</c> AND <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a
/// reachable Postgres connection string, so CI without a live gateway-Postgres
/// instance stays green. Deliberately carries NO connection string / credential
/// default of any kind — both env vars must be supplied by the runner. The suite
/// applies the same idempotent DDL as
/// <c>db/migrations/0016_init_saved_locations.sql</c> before exercising the
/// store, so a bare test database is sufficient (no separate migration-apply
/// step required), and cleans up every row it wrote.
/// </summary>
public class PostgresSavedLocationStoreTests
{
    [Fact]
    public async Task CreateAsync_FirstLocation_IsImplicitDefault_EvenWhenNotRequested()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(userId, new CreateSavedLocationRequest
            {
                Label = "Home",
                Latitude = 33.8938,
                Longitude = 35.5018,
                IsDefault = false
            }, default);

            created.IsDefault.Should().BeTrue("REQ-02: the first saved location is always the implicit default");
            created.Address.Should().BeNull("Address is optional and was not supplied");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task CreateAsync_ExplicitDefault_ClearsPreviousDefault()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var first = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Home", Latitude = 1, Longitude = 1 }, default);
            var second = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Work", Latitude = 2, Longitude = 2, IsDefault = true }, default);

            var list = await store.ListAsync(userId, default);
            list.Should().HaveCount(2);
            list.Single(l => l.Id == second.Id).IsDefault.Should().BeTrue();
            list.Single(l => l.Id == first.Id).IsDefault.Should().BeFalse();
            // ORDER BY is_default DESC, created_at ASC — the default sorts first.
            list[0].Id.Should().Be(second.Id);
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_ReturnsNull_AndDoesNotThrow()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            // Not a well-formed GUID — SavedLocation ids are client-supplied path
            // segments, so a malformed id must resolve to "not found", not throw
            // (matches InMemorySavedLocationStore's plain dictionary miss and
            // SavedLocationsEndpointTests.Update_Unknown_Id_Returns_404).
            var result = await store.UpdateAsync(userId, "does-not-exist",
                new UpdateSavedLocationRequest { Label = "X" }, default);

            result.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task UpdateAsync_UnknownId_WithIsDefaultTrue_DoesNotStripExistingDefault()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Home", Latitude = 1, Longitude = 1 }, default);
            created.IsDefault.Should().BeTrue();

            // Targets an id that doesn't exist for this user while asking for
            // isDefault=true. A failed (not-found) update must not clear the real
            // default as a side effect — this is why UpdateAsync runs inside a
            // transaction it rolls back on a zero-row result.
            var result = await store.UpdateAsync(userId, Guid.NewGuid().ToString(),
                new UpdateSavedLocationRequest { IsDefault = true }, default);

            result.Should().BeNull();

            var stillDefault = await store.GetAsync(userId, created.Id, default);
            stillDefault.Should().NotBeNull();
            stillDefault!.IsDefault.Should().BeTrue("a failed update must not have side effects");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task UpdateAsync_PartialPatch_LeavesUnspecifiedFieldsUntouched()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Home", Address = "Old", Latitude = 1, Longitude = 1 }, default);

            var updated = await store.UpdateAsync(userId, created.Id,
                new UpdateSavedLocationRequest { Address = "New Address" }, default);

            updated.Should().NotBeNull();
            updated!.Address.Should().Be("New Address");
            updated.Label.Should().Be("Home", "label was not part of the patch");
            updated.Latitude.Should().Be(1);
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovingDefault_PromotesOldestRemaining()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var first = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Home", Latitude = 1, Longitude = 1 }, default);
            await Task.Delay(5); // distinct created_at ordering tick
            var second = await store.CreateAsync(userId,
                new CreateSavedLocationRequest { Label = "Work", Latitude = 2, Longitude = 2, IsDefault = true }, default);

            var deleted = await store.DeleteAsync(userId, second.Id, default);
            deleted.Should().BeTrue();

            var remaining = await store.GetAsync(userId, first.Id, default);
            remaining.Should().NotBeNull();
            remaining!.IsDefault.Should().BeTrue("REQ-02: a user always has a default while any saved location exists");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_ReturnsFalse_AndDoesNotThrow()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var result = await store.DeleteAsync(userId, "nope", default);
            result.Should().BeFalse();
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task ListAsync_IsolatesLocationsPerUser()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return;

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userA = "test-user-a-" + Guid.NewGuid();
        var userB = "test-user-b-" + Guid.NewGuid();

        try
        {
            await store.CreateAsync(userA,
                new CreateSavedLocationRequest { Label = "A-Home", Latitude = 1, Longitude = 1 }, default);

            var bList = await store.ListAsync(userB, default);
            bList.Should().BeEmpty("user B must not see user A's saved locations");
        }
        finally
        {
            await CleanupAsync(conn, userA);
            await CleanupAsync(conn, userB);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task CleanupAsync(NpgsqlConnection conn, string userId)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM saved_locations WHERE user_id = @UserId", conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No connection
    /// string ever lives in source.
    /// </summary>
    private static async Task<(PostgresSavedLocationStore Store, NpgsqlConnection Conn)?> TryCreateStoreAsync()
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

        var store = new PostgresSavedLocationStore(factory, NullLoggerFor<PostgresSavedLocationStore>());
        return (store, conn);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0016_init_saved_locations.sql (table +
    /// indexes; schema_migrations bookkeeping is left to the real migration
    /// runner) so this suite works against a bare test database.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";
            CREATE TABLE IF NOT EXISTS saved_locations (
                id          UUID             PRIMARY KEY DEFAULT gen_random_uuid(),
                user_id     TEXT             NOT NULL,
                label       VARCHAR(80)      NOT NULL,
                address     VARCHAR(256)     NULL,
                latitude    DOUBLE PRECISION NOT NULL,
                longitude   DOUBLE PRECISION NOT NULL,
                is_default  BOOLEAN          NOT NULL DEFAULT FALSE,
                created_at  TIMESTAMPTZ      NOT NULL DEFAULT now(),
                updated_at  TIMESTAMPTZ      NOT NULL DEFAULT now()
            );
            CREATE INDEX IF NOT EXISTS idx_saved_locations_user
                ON saved_locations (user_id, created_at);
            CREATE UNIQUE INDEX IF NOT EXISTS uq_saved_locations_one_default_per_user
                ON saved_locations (user_id)
                WHERE is_default = TRUE;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
