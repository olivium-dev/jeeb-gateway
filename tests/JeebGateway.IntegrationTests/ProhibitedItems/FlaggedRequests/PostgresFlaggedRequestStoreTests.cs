using FluentAssertions;
using JeebGateway.Infrastructure;
using JeebGateway.ProhibitedItems;
using JeebGateway.ProhibitedItems.FlaggedRequests;
using JeebGateway.ProhibitedItems.Scanner;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace JeebGateway.IntegrationTests.ProhibitedItems.FlaggedRequests;

/// <summary>
/// T-backend-048 follow-up (gateway durability hardening) —
/// <see cref="PostgresFlaggedRequestStore"/>.
///
/// OPT-IN real-wire suite, mirroring the <c>PostgresAdminEscalationStoreTests</c>
/// / <c>BanServiceContractTests</c> "RealWire" pattern: runs only when
/// <c>JEEB_GW_PG_LIVE=1</c> AND <c>JEEB_GW_PG_TEST_CONNECTION</c> is set to a
/// reachable Postgres connection string, so CI without a live gateway-Postgres
/// instance stays green. Deliberately carries NO connection string / credential
/// default of any kind — both env vars must be supplied by the runner. The
/// suite applies the same idempotent DDL as
/// <c>db/migrations/0019_init_flagged_requests.sql</c> before exercising the
/// store, so a bare test database is sufficient (no separate migration-apply
/// step required), and cleans up every row it wrote.
/// </summary>
public class PostgresFlaggedRequestStoreTests
{
    [Fact]
    public async Task CreateAsync_PersistsRow_GetAsync_RoundTripsMatchesAndFields()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(new FlaggedRequestCreate
            {
                RequestId = "req-123",
                UserId = userId,
                Description = "please send this suspicious package",
                Matches = new List<ProhibitedItemMatch>
                {
                    new()
                    {
                        ItemId = "item-1",
                        ItemName = "Weapon",
                        Category = "weapons",
                        MatchedTerm = "gun",
                        Evidence = "suspicious package",
                        MatchType = ProhibitedMatchType.Fuzzy,
                        Confidence = 0.87,
                        Severity = ProhibitedSeverity.Block,
                    },
                },
            }, default);

            created.Id.Should().NotBeNullOrWhiteSpace();
            created.Status.Should().Be(FlaggedRequestStatus.Pending);
            created.RequestId.Should().Be("req-123");
            created.UserId.Should().Be(userId);
            created.Matches.Should().HaveCount(1);

            var fetched = await store.GetAsync(created.Id, default);
            fetched.Should().NotBeNull();
            fetched!.Id.Should().Be(created.Id);
            fetched.Description.Should().Be("please send this suspicious package");
            fetched.Status.Should().Be(FlaggedRequestStatus.Pending);
            fetched.Matches.Should().HaveCount(1);
            fetched.Matches[0].ItemId.Should().Be("item-1");
            fetched.Matches[0].MatchType.Should().Be(ProhibitedMatchType.Fuzzy);
            fetched.Matches[0].Severity.Should().Be(ProhibitedSeverity.Block);
            fetched.Matches[0].Confidence.Should().Be(0.87);
            fetched.DecidedBy.Should().BeNull();
            fetched.DecidedAt.Should().BeNull();
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task CreateAsync_PersistsNullRequestId_ForPreSubmissionScans()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(new FlaggedRequestCreate
            {
                RequestId = null,
                UserId = userId,
                Description = "scanned before request creation",
                Matches = Array.Empty<ProhibitedItemMatch>(),
            }, default);

            created.RequestId.Should().BeNull();
            created.Matches.Should().BeEmpty();

            var fetched = await store.GetAsync(created.Id, default);
            fetched.Should().NotBeNull();
            fetched!.RequestId.Should().BeNull();
            fetched.Matches.Should().BeEmpty();
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task ListAsync_FiltersByStatus_AndOrdersNewestFirst()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var first = await store.CreateAsync(NewCreate(userId, "first"), default);
            await Task.Delay(5); // distinct flagged_at ordering tick
            var second = await store.CreateAsync(NewCreate(userId, "second"), default);

            await store.DecideAsync(second.Id, FlaggedRequestStatus.Cleared, "admin-1", "false positive", default);

            var pendingOnly = await store.ListAsync(FlaggedRequestStatus.Pending, 1, 20, default);
            pendingOnly.Items.Should().Contain(f => f.Id == first.Id);
            pendingOnly.Items.Should().NotContain(f => f.Id == second.Id);

            var clearedOnly = await store.ListAsync(FlaggedRequestStatus.Cleared, 1, 20, default);
            clearedOnly.Items.Should().Contain(f => f.Id == second.Id);
            clearedOnly.Items.Should().NotContain(f => f.Id == first.Id);

            var all = await store.ListAsync(null, 1, 50, default);
            var indexOfFirst = IndexOfId(all.Items, first.Id);
            var indexOfSecond = IndexOfId(all.Items, second.Id);
            indexOfFirst.Should().BeGreaterOrEqualTo(0);
            indexOfSecond.Should().BeGreaterOrEqualTo(0);
            indexOfSecond.Should().BeLessThan(indexOfFirst, "ListAsync orders newest-first");
            all.Total.Should().BeGreaterOrEqualTo(2);
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task DecideAsync_UpdatesStatusReviewerAndNote_ReturnsUpdatedRow()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;
        var userId = "test-user-" + Guid.NewGuid();

        try
        {
            var created = await store.CreateAsync(NewCreate(userId, "to be upheld"), default);

            var decided = await store.DecideAsync(
                created.Id, FlaggedRequestStatus.Upheld, "admin-42", "confirmed prohibited item", default);

            decided.Should().NotBeNull();
            decided!.Status.Should().Be(FlaggedRequestStatus.Upheld);
            decided.DecidedBy.Should().Be("admin-42");
            decided.DecisionNote.Should().Be("confirmed prohibited item");
            decided.DecidedAt.Should().NotBeNull();

            var fetched = await store.GetAsync(created.Id, default);
            fetched.Should().NotBeNull();
            fetched!.Status.Should().Be(FlaggedRequestStatus.Upheld);
            fetched.DecidedBy.Should().Be("admin-42");
        }
        finally
        {
            await CleanupAsync(conn, userId);
        }
    }

    [Fact]
    public async Task DecideAsync_ReturnsNull_WhenIdNotFound()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        var result = await store.DecideAsync(
            Guid.NewGuid().ToString(), FlaggedRequestStatus.Cleared, "admin-1", null, default);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_ForMalformedId()
    {
        var ctx = await TryCreateStoreAsync();
        if (ctx is null) return; // opt-in only; not run in CI

        var (store, conn) = ctx.Value;
        await using var _ = conn;

        // Guards against Guid.Parse throwing on a bad admin-console route id —
        // must 404 (null), not crash, matching InMemoryFlaggedRequestStore's
        // dictionary-miss semantics for any non-existent key.
        var result = await store.GetAsync("not-a-guid", default);

        result.Should().BeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FlaggedRequestCreate NewCreate(string userId, string description) => new()
    {
        RequestId = "req-" + Guid.NewGuid(),
        UserId = userId,
        Description = description,
        Matches = new List<ProhibitedItemMatch>
        {
            new()
            {
                ItemId = "item-1",
                ItemName = "Item",
                Category = "misc",
                MatchedTerm = "term",
                Evidence = description,
                MatchType = ProhibitedMatchType.Exact,
                Confidence = 1.0,
                Severity = ProhibitedSeverity.Warn,
            },
        },
    };

    private static int IndexOfId(IReadOnlyList<FlaggedRequest> rows, string id)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (string.Equals(rows[i].Id, id, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static async Task CleanupAsync(NpgsqlConnection conn, string userId)
    {
        await using var cmd = new NpgsqlCommand(
            "DELETE FROM flagged_requests WHERE user_id = @UserId", conn);
        cmd.Parameters.AddWithValue("UserId", userId);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Opt-in gate — returns null (caller skips) unless JEEB_GW_PG_LIVE=1 and
    /// JEEB_GW_PG_TEST_CONNECTION are both supplied by the runner. No
    /// connection string ever lives in source.
    /// </summary>
    private static async Task<(PostgresFlaggedRequestStore Store, NpgsqlConnection Conn)?> TryCreateStoreAsync()
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

        var store = new PostgresFlaggedRequestStore(factory, NullLoggerFor<PostgresFlaggedRequestStore>());
        return (store, conn);
    }

    /// <summary>
    /// Same idempotent DDL as db/migrations/0019_init_flagged_requests.sql
    /// (table + both indexes; schema_migrations bookkeeping is left to the
    /// real migration runner) so this suite works against a bare test database.
    /// </summary>
    private static async Task EnsureSchemaAsync(NpgsqlConnection conn)
    {
        await using var cmd = new NpgsqlCommand(
            """
            CREATE EXTENSION IF NOT EXISTS "pgcrypto";
            CREATE TABLE IF NOT EXISTS flagged_requests (
                id             UUID        PRIMARY KEY DEFAULT gen_random_uuid(),
                request_id     TEXT        NULL,
                user_id        TEXT        NOT NULL,
                reason         TEXT        NOT NULL,
                matches        JSONB       NOT NULL DEFAULT '[]'::jsonb,
                status         TEXT        NOT NULL DEFAULT 'pending',
                flagged_at     TIMESTAMPTZ NOT NULL DEFAULT now(),
                reviewed_at    TIMESTAMPTZ NULL,
                reviewer_id    TEXT        NULL,
                decision_note  TEXT        NULL
            );
            CREATE INDEX IF NOT EXISTS idx_flagged_requests_flagged_at
                ON flagged_requests (flagged_at DESC);
            CREATE INDEX IF NOT EXISTS idx_flagged_requests_status_flagged_at
                ON flagged_requests (status, flagged_at DESC);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;
}
