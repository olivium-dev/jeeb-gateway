using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.JeebWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P1-12 re-cut guard: pins the serving side, the readiness guard and the production config so the
/// blind wallet-API cutover the original 6d46e69 performed cannot silently return.
/// </summary>
public sealed class WalletLedgerMigrationWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Never opened: every assertion here resolves DI or reads config, it never issues a query.
    private const string FakeWalletCs = "Host=127.0.0.1;Port=5432;Database=wallet;Username=u;Password=p";

    private readonly WebApplicationFactory<Program> _factory;

    public WalletLedgerMigrationWiringTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Fact]
    public void No_wallet_postgres_connection_string_resolves_null_reader()
    {
        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<NullJeebWalletLedgerReader>(
            "dev/CI has no wallet DB and must keep the empty-page fallback rather than hard-"
            + "depending on an unreachable wallet API (B4)");
    }

    [Fact]
    public void Shadow_disabled_resolves_postgres_reader_alone()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", "false");
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<PostgresJeebWalletLedgerReader>(
            "with the shadow off the live read path must be exactly today's Postgres projection");
    }

    [Fact]
    public void Shadow_enabled_serves_postgres_and_shadows_the_wallet_api()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", "true");
            builder.UseSetting("WalletServiceApi:BaseUrl", "http://127.0.0.1:10014");
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<ShadowComparingJeebWalletLedgerReader>();
        Primary(reader).Should().BeOfType<PostgresJeebWalletLedgerReader>(
            "Authority defaults to postgres: the wallet API must NOT serve the money-read path (B3)");
        Shadow(reader).Should().BeOfType<WalletServiceJeebWalletLedgerReader>();
    }

    [Fact]
    public void Authority_wallet_api_swaps_the_roles()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", "true");
            builder.UseSetting("WalletLedgerMigration:Authority", "wallet-api");
            builder.UseSetting("WalletServiceApi:BaseUrl", "http://127.0.0.1:10014");
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<ShadowComparingJeebWalletLedgerReader>();
        Primary(reader).Should().BeOfType<WalletServiceJeebWalletLedgerReader>(
            "the eventual flip must be a one-line env change, no further PR");
        Shadow(reader).Should().BeOfType<PostgresJeebWalletLedgerReader>();
    }

    [Fact]
    public void Shadow_enabled_without_a_wallet_api_base_url_degrades_to_postgres()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", "true");
            builder.UseSetting("WalletServiceApi:BaseUrl", string.Empty);
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<PostgresJeebWalletLedgerReader>(
            "a missing shadow target must warn and degrade, never throw at startup");
    }

    // B1 / 19-of-19 rule: the check must be registered off the connection string alone, in ANY
    // migration-flag state. The probe's own health is irrelevant here (the CS is a fake).
    [Theory]
    [InlineData("false", "postgres")]
    [InlineData("true", "postgres")]
    [InlineData("true", "wallet-api")]
    public async Task Ready_probe_registers_wallet_postgres_in_every_flag_state(
        string shadowEnabled, string authority)
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", shadowEnabled);
            builder.UseSetting("WalletLedgerMigration:Authority", authority);
        });

        using var client = factory.CreateClient();
        // 503 is expected (the connection string is fake); the payload still names every check.
        using var response = await client.GetAsync("/health/ready");

        ReadyCheckNames(await response.Content.ReadAsStringAsync())
            .Should().Contain("wallet-postgres",
                "the migration flag must never deregister a readiness check (B1)");
    }

    [Fact]
    public async Task Ready_probe_omits_wallet_postgres_when_unconfigured()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");

        ReadyCheckNames(await response.Content.ReadAsStringAsync())
            .Should().NotContain("wallet-postgres",
                "dev/CI without a wallet DB keeps its existing green readiness surface");
    }

    private static IEnumerable<string> ReadyCheckNames(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("checks")
            .EnumerateArray()
            .Select(check => check.GetProperty("name").GetString()!)
            .ToArray();
    }

    [Fact]
    public void Production_config_targets_the_native_wallet_and_serves_the_wallet_api()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(ProductionSettingsPath()));
        var root = doc.RootElement;

        root.GetProperty("WalletServiceApi").GetProperty("BaseUrl").GetString()
            .Should().Be("http://127.0.0.1:10014",
                "MSI runs native systemd; the swarm hostname also breaks the fail-closed "
                + "WalletGuard that shares this key (B2)");

        var migration = root.GetProperty("WalletLedgerMigration");
        migration.GetProperty("ShadowCompareEnabled").GetBoolean().Should().BeTrue();
        migration.GetProperty("Authority").GetString().Should().Be("wallet-api",
            "the completed flip lived only in gateway.env; a rebuilt env file or a fresh host "
            + "must not silently revert the money read to the Postgres projection (W0-05)");
    }

    // W0-05: drive the composition root off the SHIPPED Production values, not off literals, so
    // an edit to appsettings.Production.json alone cannot demote the wallet API out of the serving slot.
    [Fact]
    public void Production_config_resolves_the_wallet_api_reader_as_primary()
    {
        var production = new ConfigurationBuilder()
            .AddJsonFile(ProductionSettingsPath(), optional: false)
            .Build();

        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", FakeWalletCs);
            builder.UseSetting(
                "WalletServiceApi:BaseUrl", production["WalletServiceApi:BaseUrl"]);
            builder.UseSetting(
                "WalletLedgerMigration:ShadowCompareEnabled",
                production["WalletLedgerMigration:ShadowCompareEnabled"]);
            builder.UseSetting(
                "WalletLedgerMigration:Authority", production["WalletLedgerMigration:Authority"]);
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<ShadowComparingJeebWalletLedgerReader>();
        Primary(reader).Should().BeOfType<WalletServiceJeebWalletLedgerReader>(
            "Production must serve wallet-service; the Postgres projection fail-opens to an "
            + "empty 200 when WalletPostgres is missing, which reads as a zeroed ledger");
        Shadow(reader).Should().BeOfType<PostgresJeebWalletLedgerReader>();
    }

    private static string ProductionSettingsPath() =>
        Path.Combine(FindRepoRoot(), "src", "JeebGateway", "appsettings.Production.json");

    [Fact]
    public void No_appsettings_file_reintroduces_the_swarm_wallet_hostname()
    {
        var offenders = Directory
            .EnumerateFiles(
                Path.Combine(FindRepoRoot(), "src", "JeebGateway"), "appsettings*.json")
            .Where(path => File.ReadAllText(path).Contains("wallet-service:8080"))
            .ToArray();

        offenders.Should().BeEmpty(
            "the swarm name belongs in the .20 staging env overlay, never in checked-in config");
    }

    [Fact]
    public async Task Shadow_mismatch_is_logged_but_the_primary_response_is_served()
    {
        var primary = new StubLedgerReader(new JeebWalletLedgerEntry
        {
            Id = "a", Type = "topup", Amount = 10m, Sign = 1, Ref = "r", Ts = "2026-08-10T10:00:00Z",
        });
        var shadow = new StubLedgerReader(new JeebWalletLedgerEntry
        {
            Id = "b", Type = "topup", Amount = 99m, Sign = 1, Ref = "r", Ts = "2026-08-10T10:00:00Z",
        });
        var log = new CapturingLogger<ShadowComparingJeebWalletLedgerReader>();
        var sut = new ShadowComparingJeebWalletLedgerReader(primary, shadow, log);

        var result = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        result.Should().ContainSingle().Which.Amount.Should().Be(10m);
        (await log.WaitForMessagesAsync()).Should()
            .ContainSingle(m => m.Contains("WalletLedgerShadowMismatch"));
    }

    [Fact]
    public async Task Shadow_match_is_logged_for_the_observation_window()
    {
        var entry = new JeebWalletLedgerEntry
        {
            Id = "a", Type = "topup", Amount = 10m, Sign = 1, Ref = "r", Ts = "2026-08-10T10:00:00Z",
        };
        var log = new CapturingLogger<ShadowComparingJeebWalletLedgerReader>();
        var sut = new ShadowComparingJeebWalletLedgerReader(
            new StubLedgerReader(entry), new StubLedgerReader(entry), log);

        await sut.ReadLedgerAsync(Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        (await log.WaitForMessagesAsync()).Should()
            .ContainSingle(m => m.Contains("WalletLedgerShadowMatch"));
    }

    [Fact]
    public async Task Shadow_failure_never_fails_the_served_read()
    {
        var entry = new JeebWalletLedgerEntry
        {
            Id = "a", Type = "topup", Amount = 10m, Sign = 1, Ref = "r", Ts = "2026-08-10T10:00:00Z",
        };
        var sut = new ShadowComparingJeebWalletLedgerReader(
            new StubLedgerReader(entry),
            new ThrowingLedgerReader(),
            NullLogger<ShadowComparingJeebWalletLedgerReader>.Instance);

        var result = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        result.Should().ContainSingle();
    }

    private static IJeebWalletLedgerReader Primary(IJeebWalletLedgerReader reader) =>
        Field(reader, "_primary");

    private static IJeebWalletLedgerReader Shadow(IJeebWalletLedgerReader reader) =>
        Field(reader, "_shadow");

    private static IJeebWalletLedgerReader Field(IJeebWalletLedgerReader reader, string name)
    {
        var field = typeof(ShadowComparingJeebWalletLedgerReader).GetField(
            name,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        field.Should().NotBeNull($"{name} pins which side serves the live money read");
        return (IJeebWalletLedgerReader)field!.GetValue(reader)!;
    }

    private static string FindRepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = new FileInfo(thisFile).Directory!;
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "JeebGateway")))
            dir = dir.Parent!;

        (dir is not null).Should().BeTrue("could not locate repo root from test source file path");
        return dir!.FullName;
    }

    private sealed class StubLedgerReader(JeebWalletLedgerEntry entry) : IJeebWalletLedgerReader
    {
        public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
            Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<JeebWalletLedgerEntry>>(new[] { entry });
    }

    private sealed class ThrowingLedgerReader : IJeebWalletLedgerReader
    {
        public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
            Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
            CancellationToken ct) =>
            throw new WalletLedgerUnavailableException("shadow unavailable");
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_messages) _messages.Add(formatter(state, exception));
        }

        /// <summary>The shadow comparison is deliberately detached from the request, so its
        /// log line lands after the read returns.</summary>
        public async Task<IReadOnlyList<string>> WaitForMessagesAsync()
        {
            for (var attempt = 0; attempt < 100 && Snapshot().Count == 0; attempt++)
                await Task.Delay(20);
            return Snapshot();
        }

        private IReadOnlyList<string> Snapshot()
        {
            lock (_messages) return _messages.ToArray();
        }
    }
}
