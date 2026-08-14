using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Financials.Cod;
using JeebGateway.Migration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

// gwdbx W2-05 — the COD→wallet mirror ships INERT at "local" and is a durable, idempotent,
// watermark-bounded dual-write from dual-write-local-read up. BR-16 mapping pinned on the wire.
public sealed class CodWalletMirrorTests
{
    private static WebApplicationFactory<Program> FactoryWith(params (string Key, string Value)[] settings) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            foreach (var (key, value) in settings)
            {
                builder.UseSetting(key, value);
            }
        });

    [Fact]
    public void Unknown_CodSettlementMode_Refuses_To_Boot()
    {
        using var factory = FactoryWith(("FeatureFlags:CodSettlementMode", "upstream"));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*CodSettlementMode*", "the failure must name the flag to fix");
    }

    // W2-08 (read flip) and W2-14 (authority) are unbuilt; a rung this binary cannot serve
    // must refuse the boot rather than report a cutover that did not happen.
    [Fact]
    public void Read_Flip_Rung_Refuses_To_Boot_Until_W2_08_Builds_It()
    {
        using var factory = FactoryWith(
            ("FeatureFlags:CodSettlementMode", "dual-write-upstream-read"),
            ("CodWalletMirror:ReplayFromUtc", "2026-08-14T00:00:00Z"));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*W2-08*", "the failure must say which wave builds the missing rung");
    }

    [Fact]
    public void DualWrite_Without_Replay_Watermark_Refuses_To_Boot()
    {
        using var factory = FactoryWith(("FeatureFlags:CodSettlementMode", "dual-write-local-read"));

        var boot = () => factory.CreateClient();

        boot.Should().Throw<OptionsValidationException>()
            .WithMessage("*ReplayFromUtc*",
                "an unbounded dual-write window would either strand crash rows or invite an "
                + "unauthorised full backfill");
    }

    [Fact]
    public void DualWrite_With_Watermark_Boots_And_Registers_The_Reconciler()
    {
        using var factory = FactoryWith(
            ("FeatureFlags:CodSettlementMode", "dual-write-local-read"),
            ("CodWalletMirror:ReplayFromUtc", "2026-08-14T00:00:00Z"));

        using var client = factory.CreateClient();

        factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
            .Should().Contain(s => s is CodWalletMirrorReconciler);
    }

    // ── sweep behaviour (driven directly, no web host) ───────────────────────

    [Fact]
    public async Task Local_Rung_Sweeps_Nothing_And_Sends_Nothing()
    {
        var store = StoreWith(Row("s-1"));
        var handler = new RecordingHandler();
        var sut = Reconciler(store, handler, mode: "local", replayFrom: "2020-01-01T00:00:00Z");

        var mirrored = await sut.SweepOnceAsync(CancellationToken.None);

        mirrored.Should().Be(0, "the shipped default must change nothing");
        handler.Requests.Should().BeEmpty("inert means ZERO wallet traffic, not merely no stamp");
        (await store.GetByIdAsync(SettlementId("s-1"), default))!.WalletTxId.Should().BeNull();
    }

    [Fact]
    public async Task DryRun_Posts_Nothing_And_Stamps_Nothing()
    {
        var store = StoreWith(Row("s-1"));
        var handler = new RecordingHandler();
        var sut = Reconciler(store, handler, mode: "dual-write-local-read",
            replayFrom: "2020-01-01T00:00:00Z", dryRun: true);

        var mirrored = await sut.SweepOnceAsync(CancellationToken.None);

        mirrored.Should().Be(0);
        handler.Requests.Should().BeEmpty("dry-run is the W2-06 rehearsal: log only");
        (await store.GetByIdAsync(SettlementId("s-1"), default))!.WalletTxId.Should().BeNull();
    }

    [Fact]
    public async Task Armed_Sweep_Mirrors_Verbatim_Stamps_And_Is_Idempotent()
    {
        var store = StoreWith(Row("s-1"));
        var handler = new RecordingHandler();
        var sut = Reconciler(store, handler, mode: "dual-write-local-read",
            replayFrom: "2020-01-01T00:00:00Z");

        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(1);

        var (uri, body) = handler.Requests.Single();
        uri.AbsolutePath.Should().Be($"/v1/holders/{JeeberGuid}/earnings");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        root.GetProperty("transactionId").GetString().Should().Be(SettlementId("s-1"),
            "wallet idempotency keys off the settlement id");
        root.GetProperty("gross").GetDecimal().Should().Be(42.50m,
            "gross is the row's goods_cost — the value every gateway earnings read calls gross");
        root.GetProperty("commission").GetDecimal().Should().Be(4.25m,
            "BR-16: the STORED commission travels verbatim, never re-derived from the rate");
        root.GetProperty("commissionPercentage").GetDecimal().Should().Be(0.10m);
        root.GetProperty("type").GetString().Should().Be("delivery");
        root.GetProperty("currency").GetString().Should().Be("USD");
        root.GetProperty("paymentMethod").GetString().Should().Be("cash");
        root.GetProperty("minimumFeeApplied").GetBoolean().Should().BeFalse();
        root.GetProperty("insurance").GetDecimal().Should().Be(0.50m);
        root.GetProperty("tierName").GetString().Should().Be("standard");
        root.GetProperty("deliveredAt").GetDateTime().Should().Be(SettledAt.UtcDateTime);

        var stamped = await store.GetByIdAsync(SettlementId("s-1"), default);
        stamped!.WalletTxId.Should().Be(EarningId.ToString());

        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(0,
            "a stamped row leaves the unmirrored set — the double run is a no-op");
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Watermark_Excludes_Rows_Settled_Before_ReplayFrom()
    {
        var store = StoreWith(Row("s-1"));
        var handler = new RecordingHandler();
        var sut = Reconciler(store, handler, mode: "dual-write-local-read",
            replayFrom: "2030-01-01T00:00:00Z");

        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(0,
            "history stays untouched until the owner deliberately widens the watermark (W2-06)");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task NonGuid_JeeberId_Is_Skipped_Without_Traffic_Or_Stamp()
    {
        var oddBase = Row("s-2");
        var oddRow = new Settlement
        {
            Id = SettlementId("s-2"),
            DeliveryId = oddBase.DeliveryId,
            ClientId = oddBase.ClientId,
            JeeberId = "not-a-guid",
            TierId = oddBase.TierId,
            GoodsCost = oddBase.GoodsCost,
            CommissionTier = oddBase.CommissionTier,
            CommissionRate = oddBase.CommissionRate,
            Commission = oddBase.Commission,
            Insurance = oddBase.Insurance,
            Total = oddBase.Total,
            MinimumFeeApplied = oddBase.MinimumFeeApplied,
            Currency = oddBase.Currency,
            PaymentMethod = oddBase.PaymentMethod,
            State = oddBase.State,
            SettledAt = oddBase.SettledAt,
        };
        var store = StoreWith(oddRow);
        var handler = new RecordingHandler();
        var sut = Reconciler(store, handler, mode: "dual-write-local-read",
            replayFrom: "2020-01-01T00:00:00Z");

        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        handler.Requests.Should().BeEmpty("an unmappable holder id must not reach the wire");
        (await store.GetByIdAsync(SettlementId("s-2"), default))!.WalletTxId.Should().BeNull(
            "never stamp a fabricated wallet id");
    }

    [Fact]
    public async Task Wallet_Failure_Leaves_Row_For_The_Next_Sweep()
    {
        var store = StoreWith(Row("s-1"));
        var handler = new RecordingHandler { NextStatus = HttpStatusCode.InternalServerError };
        var sut = Reconciler(store, handler, mode: "dual-write-local-read",
            replayFrom: "2020-01-01T00:00:00Z");

        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(0);
        (await store.GetByIdAsync(SettlementId("s-1"), default))!.WalletTxId.Should().BeNull();

        handler.NextStatus = HttpStatusCode.Created;
        (await sut.SweepOnceAsync(CancellationToken.None)).Should().Be(1,
            "per-row isolation: a wallet blip only defers the mirror, never loses it");
    }

    // ── fixture ──────────────────────────────────────────────────────────────

    private static readonly Guid JeeberGuid = Guid.Parse("7b8a1c2d-3e4f-4a5b-8c6d-9e0f1a2b3c4d");
    private static readonly Guid EarningId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset SettledAt =
        new(2026, 08, 01, 12, 00, 00, TimeSpan.Zero);

    private static string SettlementId(string seed) =>
        seed == "s-1" ? "aaaaaaaa-0000-0000-0000-000000000001" : "aaaaaaaa-0000-0000-0000-000000000002";

    private static Settlement Row(string seed) => new()
    {
        Id = SettlementId(seed),
        DeliveryId = "bbbbbbbb-0000-0000-0000-000000000001",
        ClientId = "cccccccc-0000-0000-0000-000000000001",
        JeeberId = JeeberGuid.ToString(),
        TierId = "standard",
        GoodsCost = 42.50m,
        CommissionTier = CommissionTier.Standard,
        CommissionRate = 0.10m,
        Commission = 4.25m,
        Insurance = 0.50m,
        Total = 47.25m,
        MinimumFeeApplied = false,
        Currency = "USD",
        PaymentMethod = "cash",
        State = SettlementState.Settled,
        SettledAt = SettledAt,
    };

    private static InMemorySettlementStore StoreWith(params Settlement[] rows)
    {
        var store = new InMemorySettlementStore();
        foreach (var row in rows)
        {
            store.TryInsertAsync(row, CancellationToken.None).GetAwaiter().GetResult();
        }
        return store;
    }

    private static CodWalletMirrorReconciler Reconciler(
        ISettlementStore store, RecordingHandler handler, string mode, string replayFrom,
        bool dryRun = false)
    {
        var wallet = new WalletApiSettlementLedgerClient(
            new SingleClientFactory(handler), NullLogger<WalletApiSettlementLedgerClient>.Instance);
        var services = new ServiceCollection()
            .AddSingleton(store)
            .AddSingleton(wallet)
            .BuildServiceProvider();

        return new CodWalletMirrorReconciler(
            services,
            new StaticMonitor<GwdbxMigrationOptions>(new GwdbxMigrationOptions { CodSettlementMode = mode }),
            new StaticMonitor<CodWalletMirrorOptions>(new CodWalletMirrorOptions
            {
                ReplayFromUtc = replayFrom,
                DryRun = dryRun,
            }),
            TimeProvider.System,
            NullLogger<CodWalletMirrorReconciler>.Instance);
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://wallet.test/") };
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<(Uri Uri, string Body)> Requests { get; } = new();
        public HttpStatusCode NextStatus { get; set; } = HttpStatusCode.Created;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Requests.Add((request.RequestUri!, body));
            return new HttpResponseMessage(NextStatus)
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(new { earningId = EarningId }),
                    Encoding.UTF8, "application/json"),
            };
        }
    }
}
