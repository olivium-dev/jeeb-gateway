using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.JeebWallet;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// W5-10 re-cut of the P1-12 guard. The WalletPostgres seam is deleted, so wallet-service is the
/// only ledger source and no configuration may resolve a database-backed reader ever again.
/// </summary>
public sealed class WalletLedgerMigrationWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    // Never opened: every assertion resolves DI or reads the roster, it never issues a query.
    private const string RetiredWalletCs =
        "Host=127.0.0.1;Port=5432;Database=wallet;Username=u;Password=p";

    private readonly WebApplicationFactory<Program> _factory;

    public WalletLedgerMigrationWiringTests(WebApplicationFactory<Program> factory) =>
        _factory = factory;

    [Fact]
    public void No_wallet_api_configured_resolves_null_reader()
    {
        using var scope = _factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<NullJeebWalletLedgerReader>(
            "dev/CI has no wallet API and must keep the empty-page fallback rather than hard-"
            + "depending on an unreachable upstream (B4)");
    }

    [Fact]
    public void Wallet_api_authority_serves_the_wallet_service_reader()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletServiceApi:BaseUrl", "http://127.0.0.1:19999");
            builder.UseSetting("WalletLedgerMigration:Authority", "wallet-api");
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<WalletServiceJeebWalletLedgerReader>(
            "wallet-service is the sole ledger authority once WalletPostgres is deleted");
    }

    /// <summary>
    /// The regression W5-10 exists to make impossible: a stale WalletPostgres key left in a
    /// deployment must not resurrect a database read path. The key is inert, never authoritative.
    /// </summary>
    [Fact]
    public void A_stale_wallet_postgres_key_can_never_resurrect_a_database_reader()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WalletPostgres:ConnectionString", RetiredWalletCs);
            builder.UseSetting("WalletLedgerMigration:ShadowCompareEnabled", "true");
        });

        using var scope = factory.Services.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<IJeebWalletLedgerReader>();

        reader.Should().BeOfType<NullJeebWalletLedgerReader>(
            "WalletPostgres is deleted; the key is inert and must not select a DB-backed reader");
        typeof(IJeebWalletLedgerReader).Assembly
            .GetType("JeebGateway.JeebWallet.PostgresJeebWalletLedgerReader")
            .Should().BeNull("the Postgres ledger reader is deleted, not merely unwired");
    }

    /// <summary>A9 roster contract: W5-10 drops wallet-postgres, 21 -> 20.</summary>
    [Fact]
    public void Ready_roster_no_longer_declares_the_wallet_database_probe()
    {
        GatewayHealthRoster.Ready.Should().NotContain("wallet-postgres");
        GatewayHealthRoster.ExpectedReadyCount.Should().Be(20);
        GatewayHealthRoster.Ready.Should().HaveCount(GatewayHealthRoster.ExpectedReadyCount);
    }
}
