using System.Diagnostics;
using System.Runtime.CompilerServices;
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

    [Fact]
    public void PostgresReader_ServesTheDetailRoute_NotTheNullReturningInterfaceDefault()
    {
        var implementation = typeof(PostgresJeebWalletLedgerReader).GetMethod(
            nameof(IJeebWalletLedgerReader.ReadEntryAsync),
            new[] { typeof(Guid), typeof(string), typeof(CancellationToken) });

        implementation.Should().NotBeNull(
            "the interface default returns null, which the controller maps to a permanent 404 on "
            + "GET /v1/jeeb/wallet/ledger/{id} for rows the page response already returned");
    }

    [Fact]
    public void PostgresPageQuery_TieBreaksEqualTimestamps_TheSameWayAsWalletService()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "JeebGateway", "JeebWallet", "JeebWalletLedgerReader.cs"));

        source.Should().Contain("ORDER BY h.createdat DESC, d.txid DESC");
        source.Should().NotContain("ORDER BY h.createdat DESC, d.txid\n",
            "an ASC tie-break returns rows sharing a timestamp in the opposite order to "
            + "wallet-service, whose keyset cursor is only correct under DESC/DESC");
    }

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

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent!;

        (dir is not null).Should().BeTrue("could not locate repo root from test source file path");
        return dir!.FullName;
    }

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
