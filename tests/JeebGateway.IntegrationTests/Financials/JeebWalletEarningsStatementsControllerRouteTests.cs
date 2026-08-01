using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// b05/GW1 W2.6-reduced — route coverage for
/// <c>JeebWalletEarningsStatementsController</c>, the second of the two money controllers that
/// had NONE. Counted at <c>origin/main</c> 24b3dd6:
/// <c>git grep -n 'JeebWalletEarningsStatements\|earnings/statements' -- tests/</c> → no hits,
/// exit 1.
///
/// <para>The controller's headline promise is <b>"Real data only … NO rows are fabricated"</b> and
/// <b>"never a fabricated document"</b>. Those are the claims worth testing, so the assertions are
/// built around them rather than around status codes:</para>
/// <list type="bullet">
///   <item>an empty projection yields <c>{ "statements": [] }</c> — not a demo row;</item>
///   <item>the projection is fetched for the <b>bearer</b>, asserted by capturing the jeeberId the
///   controller passed down, so a controller that read someone else's rows would red;</item>
///   <item>a settlement id outside the caller's projection is a 404 and the PDF generator is
///   <b>never invoked</b> — a status assertion alone cannot tell a clean 404 from a 404 issued
///   after bytes were generated.</item>
/// </list>
/// </summary>
public class JeebWalletEarningsStatementsControllerRouteTests
{
    private const string JeeberId = "jeeber-77";
    private const string SettlementId = "settlement-abc";

    private sealed class FakeEarnings : IEarningsAggregationService
    {
        private readonly IReadOnlyList<EarningsEntry> _entries;
        public List<string> ProjectionCallsFor { get; } = new();

        public FakeEarnings(params EarningsEntry[] entries) => _entries = entries;

        public Task<EarningsProjection> GetProjectionAsync(
            string jeeberId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        {
            ProjectionCallsFor.Add(jeeberId);
            var gross = _entries.Sum(e => e.Gross);
            var commission = _entries.Sum(e => e.Commission);
            return Task.FromResult(new EarningsProjection(
                jeeberId,
                new EarningsTotals(gross - commission, gross, commission, "USD"),
                _entries,
                _entries.Count,
                from ?? DateTimeOffset.UnixEpoch,
                to ?? DateTimeOffset.UtcNow));
        }

        public Task<EarningsSummary> GetSummaryAsync(string jeeberId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(string jeeberId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<EarningsSummary> GetLifetimeSummaryAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<EarningsProjection> GetLifetimeProjectionAsync(string jeeberId, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<EarningsProjection> GetProjectionWithStatesAsync(
            string jeeberId, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<string> codStates, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPdfGenerator : IEarningsPdfGenerator
    {
        public List<EarningsStatementRequest> Calls { get; } = new();

        public Task<EarningsStatementResult> GenerateAsync(EarningsStatementRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(new EarningsStatementResult(
                Encoding.ASCII.GetBytes("%PDF-1.4 gw1-route-test"),
                "statement.pdf",
                "application/pdf"));
        }
    }

    private static EarningsEntry Entry(
        string settlementId = SettlementId,
        string deliveryId = "delivery-abc",
        decimal gross = 40.0000m,
        decimal commission = 4.0000m) =>
        new(deliveryId, settlementId, gross, commission, gross - commission, "USD",
            DateTimeOffset.UtcNow.AddDays(-2));

    private static WebApplicationFactory<Program> FactoryWith(
        IEarningsAggregationService earnings, IEarningsPdfGenerator pdf) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<IEarningsAggregationService>();
                services.AddSingleton(earnings);
                services.RemoveAll<IEarningsPdfGenerator>();
                services.AddSingleton(pdf);
            }));

    private static HttpClient JeeberClient(WebApplicationFactory<Program> f, string userId = JeeberId)
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", userId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");
        return c;
    }

    // ── The list ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Without_Identity_Is_Rejected_And_Never_Reads_A_Projection()
    {
        var earnings = new FakeEarnings(Entry());
        using var factory = FactoryWith(earnings, new RecordingPdfGenerator());

        var resp = await factory.CreateClient().GetAsync("/v1/wallet/jeeb/earnings/statements");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        earnings.ProjectionCallsFor.Should().BeEmpty(
            "an anonymous caller must not cause anyone's earnings to be read");
    }

    [Fact]
    public async Task List_As_NonJeeber_Is_Forbidden()
    {
        var earnings = new FakeEarnings(Entry());
        using var factory = FactoryWith(earnings, new RecordingPdfGenerator());

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "customer-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync("/v1/wallet/jeeb/earnings/statements");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        earnings.ProjectionCallsFor.Should().BeEmpty();
    }

    [Fact]
    public async Task List_Projects_One_Statement_Per_Real_Entry_For_The_Bearer()
    {
        var earnings = new FakeEarnings(Entry());
        using var factory = FactoryWith(earnings, new RecordingPdfGenerator());

        var resp = await JeeberClient(factory).GetAsync("/v1/wallet/jeeb/earnings/statements");

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "this 200 is the POSITIVE CONTROL for the 401/403 above");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var statements = doc.RootElement.GetProperty("statements");
        statements.GetArrayLength().Should().Be(1);

        var row = statements[0];
        row.GetProperty("id").GetString().Should().Be(SettlementId);
        row.GetProperty("currency").GetString().Should().Be("USD");
        row.GetProperty("totalPayout").GetDouble().Should().Be(36.0d, "net = gross - commission");
        row.GetProperty("deliveries").GetArrayLength().Should().Be(1);
        row.GetProperty("deliveries")[0].GetProperty("deliveryId").GetString().Should().Be("delivery-abc");

        // Identity is ALWAYS the bearer — the controller must never be steerable onto another
        // jeeber's rows. Asserting the id that reached the projection is what proves it.
        earnings.ProjectionCallsFor.Should().ContainSingle().Which.Should().Be(JeeberId);
    }

    [Fact]
    public async Task List_With_An_Empty_Projection_Returns_An_Empty_Array_Not_A_Fabricated_Row()
    {
        // The controller's own contract: "NO rows are fabricated — an empty projection yields an
        // empty { statements: [] }". This is the assertion that would catch a demo/sample row.
        var earnings = new FakeEarnings();
        using var factory = FactoryWith(earnings, new RecordingPdfGenerator());

        var resp = await JeeberClient(factory).GetAsync("/v1/wallet/jeeb/earnings/statements");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("statements").GetArrayLength().Should().Be(0);
    }

    // ── The PDF ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Pdf_For_An_Owned_Statement_Returns_The_Generated_Document()
    {
        var earnings = new FakeEarnings(Entry());
        var pdf = new RecordingPdfGenerator();
        using var factory = FactoryWith(earnings, pdf);

        var resp = await JeeberClient(factory)
            .GetAsync($"/v1/wallet/jeeb/earnings/statements/{SettlementId}/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        (await resp.Content.ReadAsByteArrayAsync()).Should().StartWith(Encoding.ASCII.GetBytes("%PDF"));

        pdf.Calls.Should().ContainSingle()
            .Which.JeeberId.Should().Be(JeeberId, "the statement is generated for the bearer, never for a path-supplied id");
    }

    [Fact]
    public async Task Pdf_For_A_Statement_Outside_The_Callers_Projection_Is_404_And_Generates_Nothing()
    {
        // The load-bearing half is `pdf.Calls` being empty. A 404 alone cannot distinguish
        // "refused before generating" from "generated a document and then threw it away" — and
        // the controller's stated contract is "never a fabricated document".
        var earnings = new FakeEarnings(Entry());
        var pdf = new RecordingPdfGenerator();
        using var factory = FactoryWith(earnings, pdf);

        var resp = await JeeberClient(factory)
            .GetAsync("/v1/wallet/jeeb/earnings/statements/someone-elses-settlement/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        pdf.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Pdf_Without_Identity_Is_Rejected_And_Generates_Nothing()
    {
        var earnings = new FakeEarnings(Entry());
        var pdf = new RecordingPdfGenerator();
        using var factory = FactoryWith(earnings, pdf);

        var resp = await factory.CreateClient()
            .GetAsync($"/v1/wallet/jeeb/earnings/statements/{SettlementId}/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        pdf.Calls.Should().BeEmpty();
        earnings.ProjectionCallsFor.Should().BeEmpty();
    }
}
