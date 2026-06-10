using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// WS-P1 (Plan 3, D1) — JEB-56/57 flag-ON UPG sync path validated against the
/// gated <see cref="MockUpgSettlementClient"/>. Test IDs T1–T7 map 1:1 onto
/// plan3/workstreams/WS-P1-UPG-MOCK-CUTOVER.md §5.
///
/// P9 framing: these tests prove (a) the mapping the gateway would send to the
/// real UPG, (b) idempotency on externalRef, (c) the DI gate makes the mock
/// unreachable unless BOTH Payments and PaymentsMock are ON, and (d) a flag-ON
/// boot WITHOUT the mock and WITHOUT a real UPG base url fails fast instead of
/// silently degrading. Production sets neither flag.
/// </summary>
public class UpgMockCutoverTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public UpgMockCutoverTests(WebApplicationFactory<Program> factory) => _factory = factory;

    private WebApplicationFactory<Program> FlagOnMockFactory() =>
        _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("FeatureFlags:UseUpstream:Payments", "true");
            b.UseSetting("FeatureFlags:UseUpstream:PaymentsMock", "true");
        });

    // ── T1 — mock records the exact mapped amounts ────────────────────────────

    [Fact] // T1
    public async Task T1_Mock_Records_Mapped_Amounts_Standard_Tier()
    {
        var mock = new MockUpgSettlementClient();
        var ledger = new UpgSettlementLedgerClient(
            mock, TimeProvider.System, NullLogger<UpgSettlementLedgerClient>.Instance);

        // tier=Standard, goods=10_000: commission = max(1000, 15%·10000) = 1500,
        // insurance = 2%·10000 = 200, total (gross cash) = 11_700.
        var breakdown = CommissionCalculator.Calculate(10_000m, CommissionTier.Standard);
        var deliveryId = $"delivery-{Guid.NewGuid()}";

        await ledger.PostLedgerEntryAsync(MakeLedgerRequest(deliveryId, "jeeber-t1", breakdown), default);

        mock.Entries.Should().ContainKey(deliveryId);
        var sent = mock.Entries[deliveryId];
        sent.Source.Should().Be(UpgSettlementLedgerClient.Source, "source is the gateway-owned channel constant");
        sent.Source.Should().Be("jeeb.cod");
        sent.ExternalRef.Should().Be(deliveryId, "externalRef is the natural idempotency key");
        sent.PayeeRef.Should().Be("jeeber-t1");
        sent.NetAmount.Should().Be(10_000m, "net to the payee = goods cost");
        sent.FeeAmount.Should().Be(1_500m + 200m, "fee = commission(15% of goods) + insurance(2% of goods)");
        sent.GrossAmount.Should().Be(11_700m, "gross = total cash collected");
        sent.Currency.Should().Be("LBP");
    }

    [Fact] // T1 (minimum-fee floor variant)
    public async Task T1_Mock_Records_Minimum_Fee_Floor()
    {
        var mock = new MockUpgSettlementClient();
        var ledger = new UpgSettlementLedgerClient(
            mock, TimeProvider.System, NullLogger<UpgSettlementLedgerClient>.Instance);

        // goods=5_000 Standard: 15% = 750 < 1000 → floor applies; insurance = 100.
        var breakdown = CommissionCalculator.Calculate(5_000m, CommissionTier.Standard);
        var deliveryId = $"delivery-{Guid.NewGuid()}";

        await ledger.PostLedgerEntryAsync(MakeLedgerRequest(deliveryId, "jeeber-t1b", breakdown), default);

        var sent = mock.Entries[deliveryId];
        breakdown.MinimumFeeApplied.Should().BeTrue();
        sent.FeeAmount.Should().Be(1_000m + 100m, "min commission 1000 LBP + 2% insurance");
        sent.NetAmount.Should().Be(5_000m);
        sent.GrossAmount.Should().Be(6_100m);
    }

    // ── T2 — idempotency on externalRef ───────────────────────────────────────

    [Fact] // T2
    public async Task T2_Same_DeliveryId_Twice_Holds_One_Entry_And_Returns_First_Id()
    {
        var mock = new MockUpgSettlementClient();
        var ledger = new UpgSettlementLedgerClient(
            mock, TimeProvider.System, NullLogger<UpgSettlementLedgerClient>.Instance);

        var breakdown = CommissionCalculator.Calculate(10_000m, CommissionTier.Standard);
        var deliveryId = $"delivery-{Guid.NewGuid()}";

        var first = await ledger.PostLedgerEntryAsync(
            MakeLedgerRequest(deliveryId, "jeeber-t2", breakdown), default);
        var second = await ledger.PostLedgerEntryAsync(
            MakeLedgerRequest(deliveryId, "jeeber-t2", breakdown), default);

        mock.Entries.Should().HaveCount(1, "replaying the same externalRef must not duplicate");
        second.LedgerEntryId.Should().Be(first.LedgerEntryId, "second call returns the first entry's id");
        first.LedgerEntryId.Should().Be(MockUpgSettlementClient.SettlementIdPrefix + deliveryId,
            "ids are deterministic so QA can replay byte-for-byte");
    }

    // ── T3 — flag matrix: Payments=OFF → in-process ledger ────────────────────

    [Fact] // T3
    public void T3_Payments_Off_Resolves_InMemory_Ledger_Client()
    {
        // Default factory: both flags false (appsettings.json defaults).
        var resolved = _factory.Services.GetRequiredService<ISettlementLedgerClient>();

        resolved.Should().BeOfType<InMemorySettlementLedgerClient>(
            "Payments=OFF keeps the current in-process prod path UNCHANGED");
    }

    [Fact] // T3 (ON+ON arm: the only combination that reaches the mock)
    public void T3_Payments_On_Mock_On_Resolves_UpgLedger_Backed_By_Mock()
    {
        using var factory = FlagOnMockFactory();

        var ledger = factory.Services.GetRequiredService<ISettlementLedgerClient>();
        var upg = factory.Services.GetRequiredService<IUpgSettlementClient>();

        ledger.Should().BeOfType<UpgSettlementLedgerClient>();
        upg.Should().BeOfType<MockUpgSettlementClient>(
            "with BOTH flags ON the UPG transport is the gated mock");
    }

    // ── T4 — flag matrix: Payments=ON, Mock=OFF, no real client → fail fast ───

    [Fact] // T4
    public void T4_Payments_On_Mock_Off_Without_BaseUrl_Fails_Fast()
    {
        using var factory = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("FeatureFlags:UseUpstream:Payments", "true");
            b.UseSetting("FeatureFlags:UseUpstream:PaymentsMock", "false");
            b.UseSetting("Services:UnifiedPayment:BaseUrl", "");
        });

        var boot = () => factory.CreateClient();

        boot.Should().Throw<Exception>(
                "flag-ON without the mock and without a real UPG base url must fail fast, " +
                "proving prod can never silently fall through to the mock")
            .Which.Message.Should().Contain("Services:UnifiedPayment:BaseUrl");
    }

    // ── T5 — flag-ON E2E: settle posts through the mock ───────────────────────

    [Fact] // T5
    public async Task T5_FlagOn_E2E_Settle_Posts_Single_Mock_Entry_With_Generic_Vocab()
    {
        using var factory = FlagOnMockFactory();

        var seed = await SeedDoneDeliveryAsync(factory);
        var http = AuthClient(factory, seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle",
            new { goodsCost = 100_000m, paymentMethod = "cash" }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var mock = factory.Services.GetRequiredService<MockUpgSettlementClient>();
        mock.Entries.Should().ContainKey(seed.Id).And.HaveCount(1, "exactly one ledger entry per settled delivery");

        var sent = mock.Entries[seed.Id];
        sent.PayeeRef.Should().Be(seed.JeeberId);
        sent.NetAmount.Should().Be(100_000m);
        sent.FeeAmount.Should().Be(15_000m + 2_000m);
        sent.GrossAmount.Should().Be(117_000m);

        // GR1 spot-check: the wire shape the gateway sends to UPG carries only
        // generic vocabulary. Property names and metadata KEYS must contain no
        // jeeb/jeeber/driver token. (VALUES are opaque to UPG; the single
        // gateway-owned channel constant "jeeb.cod" rides in the source VALUE,
        // never in a field name.)
        var wire = JsonSerializer.Serialize(sent, Json);
        using var doc = JsonDocument.Parse(wire);
        var names = new List<string>();
        CollectPropertyNames(doc.RootElement, names);
        names.Should().NotContain(
            n => Regex.IsMatch(n, "jeeb|jeeber|driver|delivery", RegexOptions.IgnoreCase),
            "UPG-bound field names and metadata keys must be product-agnostic (GR1)");
    }

    [Fact] // T5 (idempotent replay through the full HTTP surface)
    public async Task T5_FlagOn_E2E_Repeat_Settle_Does_Not_Duplicate_Mock_Entry()
    {
        using var factory = FlagOnMockFactory();

        var seed = await SeedDoneDeliveryAsync(factory);
        var http = AuthClient(factory, seed.JeeberId, "driver");

        (await http.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 100_000m }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await http.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 100_000m }, Json);

        replay.IsSuccessStatusCode.Should().BeTrue("idempotent re-settle must not 5xx");
        var mock = factory.Services.GetRequiredService<MockUpgSettlementClient>();
        mock.Entries.Keys.Where(k => k == seed.Id).Should().HaveCount(1);
    }

    // ── T6 — weekly batch (JEB-57) behavior intact at flag-ON ─────────────────
    //
    // The full clock-advance variant (test control-plane, door 2a) runs in the
    // QA surface per WS-P1 §5; here we prove the JEB-57 batch lifecycle is
    // unperturbed when the DI gate is in mock flag-ON mode: batch created
    // exactly once, re-run no duplicate, mark-paid idempotent.

    [Fact] // T6
    public async Task T6_FlagOn_Weekly_Batch_Once_NoDuplicate_MarkPaid_Idempotent()
    {
        using var factory = FlagOnMockFactory();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero));

        var store = factory.Services.GetRequiredService<ISettlementStore>();
        var batchStore = new InMemoryFallbackSettlementBatchStore(store);

        var jeeberId = $"jeeber-{Guid.NewGuid()}";
        var settledAt = new DateTimeOffset(2026, 6, 5, 12, 0, 0, TimeSpan.Zero);
        var rows = new List<Settlement>();
        for (var i = 0; i < 3; i++)
        {
            var breakdown = CommissionCalculator.Calculate(100_000m, CommissionTier.Standard);
            var (row, inserted) = await store.TryInsertAsync(new Settlement
            {
                Id = Guid.NewGuid().ToString(),
                DeliveryId = $"delivery-{Guid.NewGuid()}",
                ClientId = "client-t6",
                JeeberId = jeeberId,
                TierId = "standard",
                GoodsCost = breakdown.GoodsCost,
                CommissionTier = breakdown.Tier,
                CommissionRate = breakdown.CommissionRate,
                Commission = breakdown.Commission,
                Insurance = breakdown.Insurance,
                Total = breakdown.Total,
                MinimumFeeApplied = breakdown.MinimumFeeApplied,
                Currency = "LBP",
                PaymentMethod = "cash",
                State = SettlementState.Settled,
                CodState = CodSettlementState.Recorded,
                SettledAt = settledAt,
            }, default);
            inserted.Should().BeTrue();
            rows.Add(row);
        }

        var periodStart = new DateOnly(2026, 6, 2);
        var periodEnd = new DateOnly(2026, 6, 8);

        // First run creates the batch; second run returns the same batch.
        var batch1 = await batchStore.CreateOrGetBatchAsync(jeeberId, periodStart, periodEnd, rows, default);
        await store.MarkBatchedAsync(rows.Select(r => r.Id).ToList(), batch1.Id, clock.GetUtcNow(), default);
        var batch2 = await batchStore.CreateOrGetBatchAsync(jeeberId, periodStart, periodEnd, rows, default);

        batch2.Id.Should().Be(batch1.Id, "re-run must not create a duplicate batch for the same window");

        // Mark-paid transitions and is idempotent. (The in-memory fallback batch
        // store updates the batch; the row cascade is the settlement store's
        // MarkPaidByBatchAsync, mirroring the Postgres-backed admin flow.)
        var paid1 = await batchStore.MarkPaidAsync(batch1.Id, "admin-t6", clock.GetUtcNow(), default);
        await store.MarkPaidByBatchAsync(batch1.Id, clock.GetUtcNow(), default);
        var paid2 = await batchStore.MarkPaidAsync(batch1.Id, "admin-t6", clock.GetUtcNow(), default);
        await store.MarkPaidByBatchAsync(batch1.Id, clock.GetUtcNow(), default);

        paid1.Status.Should().Be("paid");
        paid2.Status.Should().Be("paid");
        paid2.PaidAt.Should().Be(paid1.PaidAt, "repeat mark-paid is a no-op, not a re-pay");

        var settled = await store.GetByDeliveryAsync(rows[0].DeliveryId, default);
        settled!.CodState.Should().Be(CodSettlementState.Paid);
    }

    // ── T7 — kill-switch: Payments OFF falls back cleanly ─────────────────────
    //
    // The flag is read at host build (env-flip + restart in prod). The
    // kill-switch contract proven here: the SAME settle flow that posted via
    // the mock at flag-ON completes without any 5xx on a flag-OFF host, on the
    // in-process ledger.

    [Fact] // T7
    public async Task T7_KillSwitch_FlagOff_Same_Flow_No_500s_InProcess_Ledger()
    {
        // Flag-ON leg.
        using (var onFactory = FlagOnMockFactory())
        {
            var seedOn = await SeedDoneDeliveryAsync(onFactory);
            var httpOn = AuthClient(onFactory, seedOn.JeeberId, "driver");
            var respOn = await httpOn.PostAsJsonAsync(
                $"/deliveries/{seedOn.Id}/settle", new { goodsCost = 50_000m }, Json);
            respOn.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // Kill-switch leg: flags OFF (default factory).
        _factory.Services.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<InMemorySettlementLedgerClient>("OFF reverts to the in-process ledger");

        var seed = await SeedDoneDeliveryAsync(_factory);
        var http = AuthClient(_factory, seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle", new { goodsCost = 50_000m }, Json);

        ((int)resp.StatusCode).Should().BeLessThan(500, "kill-switch path must produce no 5xx");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SettleDeliveryResponse>(Json);
        body!.LedgerEntryId.Should().NotBeNullOrEmpty("in-process ledger posted the entry");
        body.LedgerEntryId.Should().NotStartWith(MockUpgSettlementClient.SettlementIdPrefix,
            "flag-OFF must never route through the mock");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static LedgerEntryRequest MakeLedgerRequest(
        string deliveryId, string jeeberId, CommissionBreakdown breakdown) => new()
    {
        DeliveryId = deliveryId,
        JeeberId = jeeberId,
        ClientId = "client-test",
        EntryType = "cash_settlement",
        GoodsCost = breakdown.GoodsCost,
        Commission = breakdown.Commission,
        Insurance = breakdown.Insurance,
        Total = breakdown.Total,
        Currency = "LBP",
        PaymentMethod = "cash",
        IdempotencyKey = Guid.NewGuid().ToString(),
    };

    private static HttpClient AuthClient(WebApplicationFactory<Program> factory, string userId, string roles)
    {
        var c = factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", roles);
        return c;
    }

    private static async Task<Seed> SeedDoneDeliveryAsync(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "UPG mock cutover E2E",
            DropoffLocation = new GeoPoint { Lat = 24.8, Lng = 46.8 },
        }, default);
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        await store.SetStatusAsync(created.Id, CanonicalDeliveryStatus.Done, default);
        return new Seed(created.Id, clientId, jeeberId);
    }

    private static void CollectPropertyNames(JsonElement element, List<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    names.Add(prop.Name);
                    CollectPropertyNames(prop.Value, names);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectPropertyNames(item, names);
                break;
        }
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
