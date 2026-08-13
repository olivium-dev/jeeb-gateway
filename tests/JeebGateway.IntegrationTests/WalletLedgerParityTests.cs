using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.JeebWallet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// W0 shadow-window defects A / C / E: the postgres ledger reader must agree with wallet-service
/// on the absolute timestamp and serve the detail route, and the shadow must stay off the request.
/// </summary>
public sealed class WalletLedgerParityTests
{
    // Live MSI runs Europe/Amsterdam (+02:00 on this date), which is exactly the shift the
    // shadow window observed; the offsets are asserted so a no-op forcing cannot pass vacuously.
    [Theory]
    [InlineData("Europe/Amsterdam", 2)]
    [InlineData("America/New_York", -4)]
    [InlineData("UTC", 0)]
    public void PostgresTimestamp_IsLabelledUtc_NeverShiftedByTheHostZone(string hostZone, int hostOffsetHours)
    {
        using var forced = new LocalTimeZoneScope(hostZone);
        // Npgsql yields Kind=Unspecified for `timestamp` WITHOUT time zone; the column stores UTC.
        var fromDb = new DateTime(2026, 8, 8, 21, 2, 43, DateTimeKind.Unspecified).AddTicks(9_544_940);

        TimeZoneInfo.Local.GetUtcOffset(fromDb).Should().Be(
            TimeSpan.FromHours(hostOffsetHours),
            "the host zone must really be forced or this regression test proves nothing");
        PostgresJeebWalletLedgerReader.FormatUtcTimestamp(fromDb).Should().Be(
            "2026-08-08T21:02:43.9544940Z",
            "wallet-service reads the same column as `h.CreatedAt AT TIME ZONE 'UTC'`");
    }

    /// <summary>
    /// The detail route reaches real SQL rather than the null-returning interface default: an
    /// unparseable id short-circuits to null, a well-formed one is taken to the database.
    /// </summary>
    [Fact]
    public async Task PostgresReader_ServesTheDetailRoute_NotTheNullReturningInterfaceDefault()
    {
        var sut = UnreachableReader();

        (await sut.ReadEntryAsync(Guid.NewGuid(), "not-a-guid", CancellationToken.None))
            .Should().BeNull("an unparseable id can never name a row — no query is issued");
        var act = () => sut.ReadEntryAsync(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), CancellationToken.None);
        await act.Should().ThrowAsync<WalletLedgerUnavailableException>(
            "a well-formed id must be resolved against the wallet DB, not answered from memory");
    }

    /// <summary>Asserted on the SQL the reader executes, whitespace-normalised, so neither a
    /// reformat nor a semantically different ORDER BY can slip past this regression.</summary>
    [Fact]
    public void PostgresPageQuery_TieBreaksEqualTimestamps_TheSameWayAsWalletService()
    {
        var clause = Regex.Match(
            PostgresJeebWalletLedgerReader.PageSql, @"ORDER\s+BY\s+(?<clause>.+?)\s+LIMIT",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        clause.Success.Should().BeTrue("the paged read must order deterministically");
        Regex.Replace(clause.Groups["clause"].Value, @"\s+", " ").Should().Be(
            "h.createdat DESC, d.txid DESC",
            "an ASC tie-break returns rows sharing a timestamp in the opposite order to "
            + "wallet-service, whose keyset cursor is only correct under DESC/DESC");
    }

    /// <summary>Both reads must surface the same row identically, so the detail route cannot drift
    /// from the page the client already rendered.</summary>
    [Fact]
    public void PostgresDetailQuery_ProjectsAndScopesOwnership_ExactlyLikeThePageQuery()
    {
        Projection(PostgresJeebWalletLedgerReader.EntrySql).Should().Be(
            Projection(PostgresJeebWalletLedgerReader.PageSql));
        PostgresJeebWalletLedgerReader.EntrySql.Should().Contain(
            "d.sourcewalletid = ANY(@WalletIds)").And.Contain(
            "d.destinationwalletid = ANY(@WalletIds))",
            "another holder's row must not be readable by id");
    }

    /// <summary>
    /// A wallet-DB outage is not the verdict "no such transaction": 404 on a money route is a
    /// claim about the ledger's contents. The page read keeps its long-standing empty degrade.
    /// </summary>
    [Fact]
    public async Task DatabaseFailure_OnTheDetailRoute_IsNeverServedAsNotFound()
    {
        var sut = UnreachableReader();

        var detail = () => sut.ReadEntryAsync(
            Guid.NewGuid(), Guid.NewGuid().ToString("D"), CancellationToken.None);
        await detail.Should().ThrowAsync<WalletLedgerUnavailableException>();
        (await sut.ReadLedgerAsync(Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None))
            .Should().BeEmpty();
    }

    /// <summary>
    /// Once postgres is the shadow, a read cancelled by the 5s budget must be logged as a shadow
    /// FAILURE; degrading it to [] would post a fake mismatch against the flip's clean window.
    /// </summary>
    [Fact]
    public async Task CancelledRead_SurfacesAsCancellation_NotAsAnEmptyLedger()
    {
        var sut = UnreachableReader();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        var page = () => sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, cancelled.Token);

        await page.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Detached comparisons are no longer bounded by the request, so they are bounded here: a
    /// stalled shadow must not accumulate one in-flight call per request against it.
    /// </summary>
    [Fact]
    public async Task DetachedShadowReads_AreCapped_SoAStalledShadowIsNotAmplified()
    {
        var shadow = new HangingLedgerReader(TimeSpan.FromSeconds(8));
        var sut = new ShadowComparingJeebWalletLedgerReader(
            new SingleEntryLedgerReader(), shadow,
            NullLogger<ShadowComparingJeebWalletLedgerReader>.Instance);
        var cap = ShadowComparingJeebWalletLedgerReader.MaxConcurrentShadowReads;

        for (var request = 0; request < cap * 3; request++)
            await sut.ReadLedgerAsync(Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        (await shadow.WaitForStartsAsync(cap)).Should().Be(
            cap, "requests beyond the cap skip their comparison instead of piling onto the shadow");
    }

    // Defect F: the offset was int arithmetic, so a large page wrapped negative and Postgres
    // rejected it (22023) — which the reader's graceful degrade served as an empty ledger.
    [Theory]
    [InlineData(1, 20, 0L)]
    [InlineData(3, 50, 100L)]
    [InlineData(int.MaxValue, 200, 429496729200L)]
    [InlineData(int.MaxValue, 20, 42949672920L)]
    public void PageOffset_StaysPositive_AtTheIntegerCeiling(int page, int size, long expected)
    {
        PostgresJeebWalletLedgerReader.PageOffset(page, size).Should().Be(expected);
        PostgresJeebWalletLedgerReader.PageOffset(page, size).Should().BeGreaterThanOrEqualTo(
            0, "a negative OFFSET is rejected by Postgres and degrades to an empty ledger");
    }

    [Fact]
    public void PageOffset_InIntArithmetic_WouldHaveWrapped()
    {
        unchecked
        {
            ((int.MaxValue - 1) * 200).Should().BeNegative(
                "this is the pre-fix expression — the regression it proves is real, not theoretical");
        }
    }

    private static PostgresJeebWalletLedgerReader UnreachableReader() => new(
        "Host=127.0.0.1;Port=1;Username=none;Password=none;Database=none;Timeout=2;Command Timeout=2",
        NullLogger<PostgresJeebWalletLedgerReader>.Instance);

    private static string Projection(string sql) => Regex.Replace(
        Regex.Match(sql, @"SELECT(?<body>.+?)FROM", RegexOptions.Singleline).Groups["body"].Value,
        @"\s+", " ").Trim();

    [Fact]
    public async Task SlowShadow_NeverDelaysThePrimaryResponse()
    {
        var shadow = new HangingLedgerReader(TimeSpan.FromSeconds(8));
        var sut = new ShadowComparingJeebWalletLedgerReader(
            new SingleEntryLedgerReader(), shadow,
            NullLogger<ShadowComparingJeebWalletLedgerReader>.Instance);

        var watch = Stopwatch.StartNew();
        var page = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);
        var detail = await sut.ReadEntryAsync(
            Guid.NewGuid(), "a", CancellationToken.None);
        watch.Stop();

        page.Should().ContainSingle();
        detail.Should().NotBeNull();
        watch.Elapsed.Should().BeLessThan(
            TimeSpan.FromSeconds(2),
            "the shadow is observational and must never sit on the served request path");
        (await shadow.WaitForStartsAsync(2)).Should().Be(
            2, "both scopes must still shadow-read, just detached from the request");
    }

    [Fact]
    public async Task DetachedShadow_DoesNotUseTheRequestCancellationToken()
    {
        var shadow = new HangingLedgerReader(TimeSpan.FromSeconds(8));
        var sut = new ShadowComparingJeebWalletLedgerReader(
            new SingleEntryLedgerReader(), shadow,
            NullLogger<ShadowComparingJeebWalletLedgerReader>.Instance);
        using var request = new CancellationTokenSource();

        var page = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, request.Token);
        // The request token is cancelled the moment the response completes.
        request.Cancel();

        page.Should().ContainSingle();
        (await shadow.WaitForStartsAsync(1)).Should().Be(1);
        shadow.LastTokenWasRequestToken(request.Token).Should().BeFalse();
    }

    private static JeebWalletLedgerEntry Entry() => new()
    {
        Id = "a", Type = "topup", Amount = 10m, Sign = 1, Ref = "r", Ts = "2026-08-10T10:00:00Z",
    };

    /// <summary>Forces the process-local zone so the assertion never depends on the build host.</summary>
    private sealed class LocalTimeZoneScope : IDisposable
    {
        private readonly string? _previous;

        public LocalTimeZoneScope(string timeZoneId)
        {
            _previous = Environment.GetEnvironmentVariable("TZ");
            Environment.SetEnvironmentVariable("TZ", timeZoneId);
            TimeZoneInfo.ClearCachedData();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TZ", _previous);
            TimeZoneInfo.ClearCachedData();
        }
    }

    private sealed class SingleEntryLedgerReader : IJeebWalletLedgerReader
    {
        public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
            Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JeebWalletLedgerEntry>>(new[] { Entry() });

        public Task<JeebWalletLedgerEntry?> ReadEntryAsync(
            Guid holderId, string detailId, CancellationToken ct) =>
            Task.FromResult<JeebWalletLedgerEntry?>(Entry());
    }

    private sealed class HangingLedgerReader(TimeSpan hangFor) : IJeebWalletLedgerReader
    {
        private int _starts;
        private CancellationToken _lastToken;

        public async Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
            Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
            CancellationToken ct)
        {
            await HangAsync(ct);
            return Array.Empty<JeebWalletLedgerEntry>();
        }

        public async Task<JeebWalletLedgerEntry?> ReadEntryAsync(
            Guid holderId, string detailId, CancellationToken ct)
        {
            await HangAsync(ct);
            return null;
        }

        public bool LastTokenWasRequestToken(CancellationToken requestToken) =>
            _lastToken == requestToken;

        public async Task<int> WaitForStartsAsync(int expected)
        {
            for (var attempt = 0; attempt < 100 && Volatile.Read(ref _starts) < expected; attempt++)
                await Task.Delay(20);
            return Volatile.Read(ref _starts);
        }

        private async Task HangAsync(CancellationToken ct)
        {
            _lastToken = ct;
            Interlocked.Increment(ref _starts);
            await Task.Delay(hangFor, ct);
        }
    }
}
