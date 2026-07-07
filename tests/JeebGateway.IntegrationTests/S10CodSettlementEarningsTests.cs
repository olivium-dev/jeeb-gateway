using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S10 (COD settlement &amp; Jeeber earnings) gateway financials surface:
///
///   * H1/A1/A3 — settle a REAL Done delivery (the keystone: the gate now accepts
///                the canonical Done state, not just the legacy delivered/rated).
///   * H5/A4    — earnings summary nested totals envelope {net,gross,commission,currency:USD}.
///   * A5       — ETag header + If-None-Match → 304.
///   * N15      — from>to → 400.
///   * H3.3/H4/N10/N11 — COD-compose: record → by-delivery read → mark-paid
///                (idempotent re-paid → 409; cancelled batch → 422) via the
///                in-memory UPG COD client (live path swaps to the HTTP client).
///   * H6       — statement is application/pdf with the %PDF magic-byte header.
///
/// All run on the local-mirror path (FeatureFlags:UseUpstream:Delivery OFF) so the
/// IRequestsStore seed is authoritative and no live Go/Elixir upstream is needed.
/// </summary>
public class S10CodSettlementEarningsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly WebApplicationFactory<Program> _factory;

    public S10CodSettlementEarningsTests(WebApplicationFactory<Program> factory) => _factory = factory;

    // ── H1: settle a REAL Done delivery (keystone) ─────────────────────────────

    [Fact] // H1
    public async Task Settle_On_Canonical_Done_Returns_200_With_Fee_Breakdown()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done);
        var http = AuthClient(seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle",
            new { goodsCost = 2000000m, paymentMethod = "cash" }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SettleDeliveryResponse>(Json);
        body!.State.Should().Be(SettlementState.Settled);
        body.CommissionTier.Should().Be("Standard");
        body.CommissionRate.Should().Be(0.10m);
        body.Commission.Should().Be(200000m);   // 2_000_000*0.10
        body.Insurance.Should().Be(0m);
        body.Total.Should().Be(200000m);
        body.Currency.Should().Be("USD");
        body.MinimumFeeApplied.Should().BeFalse();
        body.LedgerEntryId.Should().NotBeNullOrEmpty();
    }

    [Fact] // keystone: legacy 'delivered' alias also settles (dual-read)
    public async Task Settle_On_Legacy_Delivered_Alias_Also_Returns_200()
    {
        var seed = await SeedAsync(RequestStatus.Delivered, acceptedFee: 100000m);
        var http = AuthClient(seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle", new { goodsCost = 100000m }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact] // negative: an in-flight (not Done) delivery is NOT settle-able → 409
    public async Task Settle_On_InTransit_Returns_409_Conflict()
    {
        var seed = await SeedAsync(RequestStatus.HeadingOff);
        var http = AuthClient(seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle", new { goodsCost = 100000m }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact] // A2: no minimum-fee floor on a tiny accepted fee
    public async Task Settle_Tiny_Goods_Commission_Has_No_Minimum_Floor()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done, acceptedFee: 5000m);
        var http = AuthClient(seed.JeeberId, "driver");

        var resp = await http.PostAsJsonAsync(
            $"/deliveries/{seed.Id}/settle", new { goodsCost = 5000m }, Json);

        var body = await resp.Content.ReadFromJsonAsync<SettleDeliveryResponse>(Json);
        body!.Commission.Should().Be(500m);
        body.MinimumFeeApplied.Should().BeFalse();
    }

    // ── H5 / A4: earnings nested totals envelope ───────────────────────────────

    [Fact] // H5
    public async Task EarningsSummary_Returns_Nested_Totals_With_Usd_Currency()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 2000000m }, Json)).EnsureSuccessStatusCode();

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var resp = await jeeber.GetAsync($"/api/earnings/summary?from={from}&to={to}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var totals = doc.RootElement.GetProperty("totals");
        totals.GetProperty("currency").GetString().Should().Be("USD");
        totals.GetProperty("gross").GetDecimal().Should().Be(2000000m);
        totals.GetProperty("commission").GetDecimal().Should().Be(200000m);
        totals.GetProperty("net").GetDecimal().Should().Be(1800000m); // gross - commission
        doc.RootElement.GetProperty("entries").GetArrayLength().Should().Be(1);
        // additive legacy flat keys preserved
        doc.RootElement.TryGetProperty("jeeberId", out _).Should().BeTrue();
    }

    [Fact] // A4.2: lifetime read carries the same nested envelope
    public async Task EarningsLifetime_Returns_Nested_Totals()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done, acceptedFee: 1000000m);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 1000000m }, Json)).EnsureSuccessStatusCode();

        var resp = await jeeber.GetAsync("/api/earnings/lifetime");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("totals").GetProperty("currency").GetString().Should().Be("USD");
    }

    // ── A5: ETag / 304 ─────────────────────────────────────────────────────────

    [Fact] // A5
    public async Task EarningsSummary_Emits_ETag_And_304_On_Conditional_Read()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done, acceptedFee: 500000m);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 500000m }, Json)).EnsureSuccessStatusCode();

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var first = await jeeber.GetAsync($"/api/earnings/summary?from={from}&to={to}");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.TryGetValues(HeaderNames.ETag, out var tags) ? tags.First() : null;
        etag.Should().NotBeNullOrEmpty();

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/earnings/summary?from={from}&to={to}");
        req.Headers.TryAddWithoutValidation(HeaderNames.IfNoneMatch, etag);
        var second = await jeeber.SendAsync(req);
        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
    }

    // ── N15: reversed range → 400 ──────────────────────────────────────────────

    [Fact] // N15
    public async Task EarningsSummary_From_Greater_Than_To_Returns_400()
    {
        var jeeber = AuthClient($"jeeber-{Guid.NewGuid()}", "driver");
        var resp = await jeeber.GetAsync("/api/earnings/summary?from=2026-06-30&to=2026-06-01");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── COD compose: record → by-delivery → mark-paid (in-memory UPG) ──────────

    [Fact] // H3.3 + H1 prerequisite: record then read back the batched COD
    public async Task Cod_Record_Then_ReadBack_By_Delivery_Returns_Batched()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 2000000m }, Json)).EnsureSuccessStatusCode();

        var record = await jeeber.PostAsJsonAsync(
            "/api/v1/payments/cod/record", new { deliveryId = seed.Id }, Json);
        record.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.Created);

        var read = await jeeber.GetAsync($"/api/v1/payments/cod_jeeb/by-delivery/{seed.Id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("status").GetString().Should().Be("batched");
        doc.RootElement.GetProperty("batchId").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact] // COD by-delivery read by a non-party → 403
    public async Task Cod_ReadBack_NonParty_Returns_403()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done, acceptedFee: 1000000m);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 1000000m }, Json)).EnsureSuccessStatusCode();
        (await jeeber.PostAsJsonAsync("/api/v1/payments/cod/record",
            new { deliveryId = seed.Id }, Json)).EnsureSuccessStatusCode();

        var stranger = AuthClient($"stranger-{Guid.NewGuid()}", "driver,customer");
        var read = await stranger.GetAsync($"/api/v1/payments/cod_jeeb/by-delivery/{seed.Id}");
        read.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // H4: admin marks the batch paid → 200; N10: re-paid → 409
    public async Task MarkPaid_By_Admin_Returns_200_Then_409_On_Repaid()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 2000000m }, Json)).EnsureSuccessStatusCode();

        var record = await jeeber.PostAsJsonAsync(
            "/api/v1/payments/cod/record", new { deliveryId = seed.Id }, Json);
        using var rdoc = JsonDocument.Parse(await record.Content.ReadAsStringAsync());
        var batchId = rdoc.RootElement.GetProperty("data").GetProperty("batchId").GetString();
        batchId.Should().NotBeNullOrEmpty();

        var admin = AuthClient($"admin-{Guid.NewGuid()}", "admin");
        var paid = await admin.PostAsJsonAsync(
            $"/admin/v1/settlements/{batchId}/mark-paid", new { eventType = "settlement.paid" }, Json);
        paid.StatusCode.Should().Be(HttpStatusCode.OK);
        using var pdoc = JsonDocument.Parse(await paid.Content.ReadAsStringAsync());
        pdoc.RootElement.GetProperty("status").GetString().Should().Be("paid");

        var repaid = await admin.PostAsJsonAsync(
            $"/admin/v1/settlements/{batchId}/mark-paid", new { eventType = "settlement.paid" }, Json);
        repaid.StatusCode.Should().Be(HttpStatusCode.Conflict); // N10
    }

    [Fact] // N12: a non-admin cannot mark a batch paid → 403
    public async Task MarkPaid_By_NonAdmin_Returns_403()
    {
        var jeeber = AuthClient($"jeeber-{Guid.NewGuid()}", "driver");
        var resp = await jeeber.PostAsJsonAsync(
            "/admin/v1/settlements/batch-x/mark-paid", new { eventType = "settlement.paid" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact] // mark-paid unauthenticated → 401
    public async Task MarkPaid_Unauthenticated_Returns_401()
    {
        var http = _factory.CreateClient();
        var resp = await http.PostAsJsonAsync(
            "/admin/v1/settlements/batch-x/mark-paid", new { eventType = "settlement.paid" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── H6: statement is a real PDF ────────────────────────────────────────────

    [Fact] // H6 (en)
    public async Task Statement_Returns_Application_Pdf_With_Magic_Bytes()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 2000000m }, Json)).EnsureSuccessStatusCode();

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var resp = await jeeber.GetAsync($"/api/earnings/statement?from={from}&to={to}&language=en");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        Encoding.ASCII.GetString(bytes, 0, 4).Should().Be("%PDF");
    }

    [Fact] // H6 (ar): the bilingual path also produces a PDF
    public async Task Statement_Arabic_Returns_Application_Pdf()
    {
        var seed = await SeedAsync(CanonicalDeliveryStatus.Done, acceptedFee: 750000m);
        var jeeber = AuthClient(seed.JeeberId, "driver");
        (await jeeber.PostAsJsonAsync($"/deliveries/{seed.Id}/settle",
            new { goodsCost = 750000m }, Json)).EnsureSuccessStatusCode();

        var from = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("O"));
        var to = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("O"));
        var resp = await jeeber.GetAsync($"/api/earnings/statement?from={from}&to={to}&language=ar");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await resp.Content.ReadAsByteArrayAsync()).Length.Should().BeGreaterThan(100);
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private HttpClient AuthClient(string userId, string roles)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", roles);
        return c;
    }

    private async Task<Seed> SeedAsync(string status, decimal acceptedFee = 2000000m)
    {
        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var clientId = $"client-{Guid.NewGuid()}";
        var jeeberId = $"jeeber-{Guid.NewGuid()}";

        var created = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up the package",
            DropoffLocation = new GeoPoint { Lat = 24.8, Lng = 46.8 }
        }, default);
        var accepted = await store.TryAcceptByJeeberAsync(
            created.Id, jeeberId, limit: int.MaxValue, at: DateTimeOffset.UtcNow, ct: default);
        accepted.Should().NotBeNull();
        (await store.TrySetAcceptedFeeAsync(created.Id, acceptedFee, default)).Should().BeTrue();
        await store.SetStatusAsync(created.Id, status, default);
        return new Seed(created.Id, clientId, jeeberId);
    }

    private sealed record Seed(string Id, string ClientId, string JeeberId);
}
