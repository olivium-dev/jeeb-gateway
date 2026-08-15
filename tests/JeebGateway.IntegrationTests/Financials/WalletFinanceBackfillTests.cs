using FluentAssertions;
using JeebGateway.Financials;
using WalletFinanceBackfill;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

public sealed class WalletFinanceBackfillTests
{
    private static readonly Guid JeeberOne = Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid JeeberTwo = Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Execute_Provisions_The_Full_Um_Population_Before_Settlement_Posts()
    {
        var source = new StubSource(
            new[] { JeeberOne, JeeberTwo },
            new[] { Settlement(JeeberOne) });
        var provisioner = new StubProvisioner(defaultReady: false);
        var ledger = new StubLedger();
        var rows = new List<object>();
        var runner = new BackfillRunner(Options(execute: true), ledger, provisioner, source, rows.Add);

        var summary = await runner.RunAsync(CancellationToken.None);

        provisioner.Inspected.Should().Equal(JeeberOne, JeeberTwo);
        provisioner.Ensured.Should().Equal(JeeberOne, JeeberTwo);
        ledger.Posted.Select(request => Guid.Parse(request.JeeberId)).Should().Equal(JeeberOne);
        summary.AuthoritativeJeeberHolders.Should().Be(2);
        summary.WalletHoldersEnsured.Should().Be(2);
        summary.WalletPostsSucceeded.Should().Be(1);
        rows.OfType<HolderBackfillRow>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Settlement_Row_Cannot_Infer_A_Holder_Outside_Um_Authority()
    {
        var source = new StubSource(
            new[] { JeeberOne },
            new[] { Settlement(JeeberTwo) });
        var provisioner = new StubProvisioner(defaultReady: true);
        var ledger = new StubLedger();
        var rows = new List<object>();
        var runner = new BackfillRunner(Options(execute: true), ledger, provisioner, source, rows.Add);

        var summary = await runner.RunAsync(CancellationToken.None);

        provisioner.Inspected.Should().Equal(JeeberOne);
        provisioner.Ensured.Should().Equal(JeeberOne);
        ledger.Posted.Should().BeEmpty();
        summary.Errors.Should().Be(1);
        summary.ReconciliationMismatches.Should().BeGreaterThan(0);
        rows.OfType<BackfillRow>().Single().Error.Should()
            .Contain("absent from the active user-management driver population");
    }

    [Fact]
    public async Task Dry_Run_Inspects_Every_Um_Jeeber_And_Performs_No_Writes()
    {
        var source = new StubSource(new[] { JeeberOne, JeeberTwo }, Array.Empty<GatewaySettlement>());
        var provisioner = new StubProvisioner(defaultReady: true);
        provisioner.Readiness[JeeberTwo] = false;
        var ledger = new StubLedger();
        var runner = new BackfillRunner(Options(execute: false), ledger, provisioner, source);

        var summary = await runner.RunAsync(CancellationToken.None);

        provisioner.Inspected.Should().Equal(JeeberOne, JeeberTwo);
        provisioner.Ensured.Should().BeEmpty();
        ledger.Posted.Should().BeEmpty();
        summary.ReconciliationMismatches.Should().Be(1);
    }

    [Fact]
    public void Population_Query_Is_Um_Role_Authoritative_And_Excludes_Removed_Users()
    {
        NpgsqlBackfillSource.AuthoritativeJeeberSql.Should().Contain("\"AvailableRoles\"");
        NpgsqlBackfillSource.AuthoritativeJeeberSql.Should().Contain("ARRAY['driver']");
        NpgsqlBackfillSource.AuthoritativeJeeberSql.Should().Contain("\"Email\" <> 'Removed'");
        NpgsqlBackfillSource.AuthoritativeJeeberSql.Should().Contain("\"Username\" <> 'Removed'");
        NpgsqlBackfillSource.AuthoritativeJeeberSql.Should().NotContain("settlements");
    }

    [Fact]
    public void Command_Line_Requires_An_Explicit_UserManagement_Secret_Source()
    {
        var parse = () => WalletFinanceBackfill.Options.Parse(new[]
        {
            "--gateway-dsn-env", "UNUSED_GATEWAY_ENV",
            "--delivery-dsn-env", "UNUSED_DELIVERY_ENV",
            "--wallet-base-url", "http://wallet.test",
        });

        parse.Should().Throw<ArgumentException>()
            .WithMessage("*--user-management-dsn-env is required*");
    }

    private static Options Options(bool execute) => new()
    {
        UserManagementDsn = "unused-um",
        GatewayDsn = "unused-gateway",
        DeliveryDsn = "unused-delivery",
        WalletBaseUrl = "http://wallet.test",
        Execute = execute,
    };

    private static GatewaySettlement Settlement(Guid jeeberId) => new(
        "settlement-1",
        "delivery-1",
        jeeberId.ToString("D"),
        Guid.NewGuid().ToString("D"),
        100m,
        10m,
        5m,
        15m,
        "USD",
        "cash",
        DateTimeOffset.Parse("2026-08-10T00:00:00Z"),
        null);

    private sealed class StubSource(
        IReadOnlyList<Guid> authoritativeIds,
        IReadOnlyList<GatewaySettlement> settlements) : IBackfillSource
    {
        public Task<IReadOnlyList<Guid>> ReadAuthoritativeJeeberIdsAsync(CancellationToken ct) =>
            Task.FromResult(authoritativeIds);

        public Task<IReadOnlyList<GatewaySettlement>> ReadGatewaySettlementsAsync(CancellationToken ct) =>
            Task.FromResult(settlements);

        public Task<IReadOnlyList<DeliveryMarker>> ReadDeliveryMarkersAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DeliveryMarker>>(Array.Empty<DeliveryMarker>());
    }

    private sealed class StubProvisioner(bool defaultReady) : IWalletProvisioner
    {
        public Dictionary<Guid, bool> Readiness { get; } = new();
        public List<Guid> Inspected { get; } = new();
        public List<Guid> Ensured { get; } = new();

        public Task<IReadOnlyList<WalletCurrency>> ReadCurrenciesAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<WalletCurrency>>(new[]
            {
                new WalletCurrency { Id = 2, Code = "USD" },
            });

        public Task<WalletInspection> InspectAsync(
            Guid holderId,
            IReadOnlyList<WalletCurrency> currencies,
            CancellationToken ct)
        {
            Inspected.Add(holderId);
            var ready = Readiness.GetValueOrDefault(holderId, defaultReady);
            return Task.FromResult(new WalletInspection(
                new WalletHolderResponse(), ready, ready ? "ready" : "holder_missing"));
        }

        public Task EnsureAsync(
            Guid holderId,
            IReadOnlyList<WalletCurrency> currencies,
            WalletInspection inspection,
            CancellationToken ct)
        {
            Ensured.Add(holderId);
            return Task.CompletedTask;
        }
    }

    private sealed class StubLedger : ISettlementLedgerClient
    {
        public List<LedgerEntryRequest> Posted { get; } = new();

        public Task<LedgerEntryResponse> PostLedgerEntryAsync(
            LedgerEntryRequest request,
            CancellationToken ct)
        {
            Posted.Add(request);
            return Task.FromResult(new LedgerEntryResponse
            {
                LedgerEntryId = "wallet-header-1",
                PostedAt = DateTimeOffset.Parse("2026-08-10T00:01:00Z"),
            });
        }
    }
}
