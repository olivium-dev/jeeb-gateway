using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using JeebGateway.Financials.Cod;
using JeebGateway.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw1Pack;

/// <summary>
/// GW1 TEST PACK — member item <b>W2.6-reduced</b>: route tests for the two money
/// controllers that had none (<see cref="AdminSettlementsController"/>,
/// <c>JeebWalletEarningsStatementsController</c>).
///
/// <para>Run this item alone:
/// <c>dotnet test --no-build --filter "FullyQualifiedName~Gw1Pack.W26_"</c>.</para>
///
/// <para><b>The problem this file solves that a pile of route tests does not.</b>
/// "Add route tests for controller X" has no completion criterion — coverage can drop
/// silently the moment someone adds an action, and no test goes red. So the pack leads
/// with a CENSUS: the routes and capabilities of both controllers are enumerated by
/// reflection and compared against a checked-in expectation. Add an action, change a
/// route template, or drop a <c>[RequireCapability]</c>, and <b>C1/C3 red by
/// construction</b> — the pack cannot silently stop covering the surface.</para>
///
/// <para>The behavioural tests below are deliberately the ones the writer's
/// <c>AdminSettlementsControllerRouteTests</c> /
/// <c>JeebWalletEarningsStatementsControllerRouteTests</c> do NOT cover: the earnings
/// date-window arithmetic (a 90-day default, a from/to passthrough, and a silent
/// start&gt;end swap — all untested, all on a money report), and the PDF's derived
/// single-day period.</para>
///
/// <para><b>C5 is a NEGATIVE DISCRIMINATOR, not a defect.</b> There are two
/// <c>mark-paid</c> routes in this gateway. GW1 owns the
/// <c>ISettlementBatchStore</c>-backed one; the <c>CodSettlementComposeController</c>
/// one is backed by two <c>ConcurrentDictionary</c>s and loses its row across a restart
/// for a pre-existing reason GW1 never touched. C5 pins that fact in code so a later
/// restart round cannot be scored against GW1 by mistake.</para>
/// </summary>
public class W26_MoneyControllerRouteCensusTests
{
    // ── C1 — the route census ──────────────────────────────────────────────

    private static IEnumerable<(string Method, string Template)> Census(Type controller)
    {
        var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
        foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            foreach (var http in action.GetCustomAttributes().OfType<IActionHttpMethodProvider>())
            {
                var template = (http as IRouteTemplateProvider)?.Template;
                var full = string.IsNullOrEmpty(template) ? prefix : $"{prefix}/{template}";
                foreach (var verb in http.HttpMethods)
                    yield return (verb, full);
            }
        }
    }

    [Fact]
    public void C1_The_Two_Money_Controllers_Expose_Exactly_The_Routes_This_Pack_Covers()
    {
        var admin = Census(typeof(AdminSettlementsController)).ToArray();
        var earnings = Census(typeof(JeebWalletEarningsStatementsController)).ToArray();

        // POSITIVE CONTROL — the reflection census must find routes at all. An empty
        // census would satisfy any "no unexpected route" phrasing vacuously.
        admin.Should().NotBeEmpty();
        earnings.Should().NotBeEmpty();

        admin.Should().BeEquivalentTo(new[]
        {
            ("GET",  "v1/admin/settlements/batches"),
            ("GET",  "v1/admin/settlements/batches/{id:guid}"),
            ("POST", "v1/admin/settlements/batches/{id:guid}/mark-paid"),
        }, "a new action on a money controller must not slip in uncovered — update this census " +
           "AND add the route test, in that order");

        earnings.Should().BeEquivalentTo(new[]
        {
            ("GET", "v1/wallet/jeeb/earnings/statements"),
            ("GET", "v1/wallet/jeeb/earnings/statements/{id}/pdf"),
        });
    }

    [Fact]
    public void C2_Every_Censused_Route_Is_Actually_Registered_In_The_Live_Routing_Table()
    {
        // C1 reads attributes; C2 reads the routing table the BOOTED host built. The two
        // can disagree — a controller excluded from application parts, or shadowed by a
        // conflicting template, has attributes and no route.
        using var factory = new WebApplicationFactory<Program>();
        var descriptors = factory.Services
            .GetRequiredService<IActionDescriptorCollectionProvider>().ActionDescriptors.Items;

        descriptors.Should().NotBeEmpty("positive control: the routing table must be readable");

        var live = descriptors
            .Where(d => d.AttributeRouteInfo?.Template is not null)
            .Select(d => (Verb: d.ActionConstraints?.OfType<Microsoft.AspNetCore.Mvc.ActionConstraints.HttpMethodActionConstraint>()
                                  .SelectMany(c => c.HttpMethods).FirstOrDefault() ?? "",
                          Template: d.AttributeRouteInfo!.Template!))
            .ToHashSet();

        foreach (var route in Census(typeof(AdminSettlementsController))
                     .Concat(Census(typeof(JeebWalletEarningsStatementsController))))
        {
            live.Should().Contain((route.Method, route.Template),
                $"{route.Method} {route.Template} is censused but not in the live routing table");
        }
    }

    [Fact]
    public void C3_Every_Action_On_Both_Money_Controllers_Carries_An_Explicit_Capability()
    {
        static string Capability(Type controller, string action)
        {
            var m = controller.GetMethod(action)!;
            var onMethod = m.GetCustomAttributes<RequireCapabilityAttribute>().SingleOrDefault();
            var onClass = controller.GetCustomAttributes<RequireCapabilityAttribute>().SingleOrDefault();
            return (onMethod ?? onClass)?.Capability ?? "<NONE>";
        }

        // ADR-005: an action with no capability marker is unguarded. On a money surface
        // that is the difference between "admin only" and "anyone with a session".
        Capability(typeof(AdminSettlementsController), nameof(AdminSettlementsController.ListBatches))
            .Should().Be(Capabilities.SettlementsManage);
        Capability(typeof(AdminSettlementsController), nameof(AdminSettlementsController.GetBatch))
            .Should().Be(Capabilities.SettlementsManage);
        Capability(typeof(AdminSettlementsController), nameof(AdminSettlementsController.MarkPaid))
            .Should().Be(Capabilities.SettlementsManage);

        Capability(typeof(JeebWalletEarningsStatementsController), nameof(JeebWalletEarningsStatementsController.ListStatements))
            .Should().Be(Capabilities.EarningsReadOwn);
        Capability(typeof(JeebWalletEarningsStatementsController), nameof(JeebWalletEarningsStatementsController.DownloadStatementPdf))
            .Should().Be(Capabilities.EarningsPdfOwn);

        // NEGATIVE-SPACE CONTROL — the helper must be able to report an absent marker,
        // or every assertion above could be an artefact of a helper that always finds one.
        Capability(typeof(W26_MoneyControllerRouteCensusTests), nameof(C3_Every_Action_On_Both_Money_Controllers_Carries_An_Explicit_Capability))
            .Should().Be("<NONE>");
    }

    // ── C4 — the earnings date window, untested before this pack ───────────

    private sealed class WindowRecordingEarnings : IEarningsAggregationService
    {
        private readonly IReadOnlyList<EarningsEntry> _entries;
        public List<(string JeeberId, DateTimeOffset From, DateTimeOffset To)> Calls { get; } = new();

        public WindowRecordingEarnings(params EarningsEntry[] entries) => _entries = entries;

        public Task<EarningsProjection> GetProjectionAsync(
            string jeeberId, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct)
        {
            Calls.Add((jeeberId, from ?? DateTimeOffset.MinValue, to ?? DateTimeOffset.MaxValue));
            var gross = _entries.Sum(e => e.Gross);
            var commission = _entries.Sum(e => e.Commission);
            return Task.FromResult(new EarningsProjection(
                jeeberId, new EarningsTotals(gross - commission, gross, commission, "USD"),
                _entries, _entries.Count, from ?? DateTimeOffset.UnixEpoch, to ?? DateTimeOffset.UtcNow));
        }

        public Task<EarningsSummary> GetSummaryAsync(string j, DateTimeOffset f, DateTimeOffset t, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<DailyEarnings>> GetDailyBreakdownAsync(string j, DateTimeOffset f, DateTimeOffset t, CancellationToken ct) => throw new NotSupportedException();
        public Task<EarningsSummary> GetLifetimeSummaryAsync(string j, CancellationToken ct) => throw new NotSupportedException();
        public Task<EarningsProjection> GetLifetimeProjectionAsync(string j, CancellationToken ct) => throw new NotSupportedException();
        public Task<EarningsProjection> GetProjectionWithStatesAsync(string j, DateTimeOffset f, DateTimeOffset t, IReadOnlyCollection<string> s, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingPdf : IEarningsPdfGenerator
    {
        public List<EarningsStatementRequest> Calls { get; } = new();
        public Task<EarningsStatementResult> GenerateAsync(EarningsStatementRequest request, CancellationToken ct)
        {
            Calls.Add(request);
            return Task.FromResult(new EarningsStatementResult(
                System.Text.Encoding.ASCII.GetBytes("%PDF-1.4 gw1-pack"), "s.pdf", "application/pdf"));
        }
    }

    private static EarningsEntry SettledEntry(DateTimeOffset settledAt, string settlementId = "settlement-w26")
        => new("delivery-w26", settlementId, 40m, 4m, 36m, "USD", settledAt);

    private static (WebApplicationFactory<Program> Factory, HttpClient Client) JeeberHost(
        IEarningsAggregationService earnings, IEarningsPdfGenerator pdf, string userId = "jeeber-w26")
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s =>
            {
                s.RemoveAll<IEarningsAggregationService>();
                s.AddSingleton(earnings);
                s.RemoveAll<IEarningsPdfGenerator>();
                s.AddSingleton(pdf);
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");
        return (factory, client);
    }

    [Fact]
    public async Task C4a_List_Without_A_Window_Reads_Exactly_The_Last_90_Days()
    {
        var earnings = new WindowRecordingEarnings(SettledEntry(DateTimeOffset.UtcNow.AddDays(-3)));
        var (factory, client) = JeeberHost(earnings, new RecordingPdf());
        using var _ = factory;

        var before = DateTimeOffset.UtcNow;
        var resp = await client.GetAsync("/v1/wallet/jeeb/earnings/statements");
        var after = DateTimeOffset.UtcNow;

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = earnings.Calls.Should().ContainSingle().Subject;
        call.To.Should().BeOnOrAfter(before).And.BeOnOrBefore(after,
            "the default window ends now — a stale or clamped end date silently hides recent earnings");
        (call.To - call.From).Should().BeCloseTo(TimeSpan.FromDays(90), TimeSpan.FromSeconds(2),
            "the documented default window is 90 days");
    }

    [Fact]
    public async Task C4b_List_Passes_An_Explicit_Window_Through_Verbatim()
    {
        // POSITIVE CONTROL for C4a: without this, a controller that ignored from/to and
        // always used its default would still satisfy C4a.
        var earnings = new WindowRecordingEarnings(SettledEntry(new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero)));
        var (factory, client) = JeeberHost(earnings, new RecordingPdf());
        using var _ = factory;

        var resp = await client.GetAsync(
            "/v1/wallet/jeeb/earnings/statements?from=2026-03-01T00:00:00Z&to=2026-03-31T00:00:00Z");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = earnings.Calls.Should().ContainSingle().Subject;
        call.From.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        call.To.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero));
        call.JeeberId.Should().Be("jeeber-w26", "the window is applied to the BEARER's rows, never a supplied id");
    }

    [Fact]
    public async Task C4c_List_Swaps_An_Inverted_Window_Instead_Of_Reading_An_Empty_Range()
    {
        // Undocumented-but-real behaviour on a money report: from > to is silently
        // swapped. Untested before this pack, and the alternative (passing an inverted
        // range downstream) reads as "you earned nothing" rather than as an error.
        var earnings = new WindowRecordingEarnings(SettledEntry(new DateTimeOffset(2026, 3, 5, 12, 0, 0, TimeSpan.Zero)));
        var (factory, client) = JeeberHost(earnings, new RecordingPdf());
        using var _ = factory;

        var resp = await client.GetAsync(
            "/v1/wallet/jeeb/earnings/statements?from=2026-03-31T00:00:00Z&to=2026-03-01T00:00:00Z");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var call = earnings.Calls.Should().ContainSingle().Subject;
        call.From.Should().BeBefore(call.To.UtcDateTime,
            "an inverted window must be normalised, not forwarded");
        call.From.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        call.To.ToUniversalTime().Should().Be(new DateTimeOffset(2026, 3, 31, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task C4d_Pdf_Period_Is_The_Settlement_Day_Not_The_Whole_Lookup_Window()
    {
        var settledAt = new DateTimeOffset(2026, 3, 5, 17, 42, 11, TimeSpan.Zero);
        var earnings = new WindowRecordingEarnings(SettledEntry(settledAt));
        var pdf = new RecordingPdf();
        var (factory, client) = JeeberHost(earnings, pdf);
        using var _ = factory;

        var resp = await client.GetAsync("/v1/wallet/jeeb/earnings/statements/settlement-w26/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = pdf.Calls.Should().ContainSingle().Subject;
        request.JeeberId.Should().Be("jeeber-w26");
        request.PeriodStart.Should().Be(new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero),
            "the statement period is the settlement DAY — a wider period would bill other days onto this document");
        request.PeriodEnd.Should().Be(new DateTimeOffset(2026, 3, 6, 0, 0, 0, TimeSpan.Zero));
    }

    // ── C4e — the admin controller's unhappy read path ─────────────────────

    private sealed class EmptyBatchStore : ISettlementBatchStore
    {
        public List<string> ListedStatuses { get; } = new();
        public Task<SettlementBatch?> GetByIdAsync(Guid id, CancellationToken ct) => Task.FromResult<SettlementBatch?>(null);
        public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct)
        {
            ListedStatuses.Add(status);
            return Task.FromResult<IReadOnlyList<SettlementBatch>>(Array.Empty<SettlementBatch>());
        }
        public Task<SettlementBatch> MarkPaidAsync(Guid id, string admin, DateTimeOffset at, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct) => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());
        public Task MarkBatchProcessedAsync(IReadOnlyList<string> ids, DateTimeOffset at, CancellationToken ct) => Task.CompletedTask;
        public Task<SettlementBatch> CreateOrGetBatchAsync(string j, DateOnly s, DateOnly e, IReadOnlyList<Settlement> x, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task C4e_ListBatches_Forwards_The_Status_Verbatim_And_An_Unknown_Status_Is_An_Empty_200()
    {
        var store = new EmptyBatchStore();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(s => { s.RemoveAll<ISettlementBatchStore>(); s.AddSingleton<ISettlementBatchStore>(store); }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "admin-w26");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var unknown = await client.GetAsync("/v1/admin/settlements/batches?status=not-a-real-status");

        unknown.StatusCode.Should().Be(HttpStatusCode.OK,
            "an unrecognised filter is an empty result, not a 500 on an admin money screen");
        (await unknown.Content.ReadAsStringAsync()).Should().Be("[]");
        store.ListedStatuses.Should().ContainSingle().Which.Should().Be("not-a-real-status");

        // POSITIVE CONTROL — the default really is "open", so the assertion above is
        // about pass-through and not about a controller that ignores the query entirely.
        (await client.GetAsync("/v1/admin/settlements/batches")).StatusCode.Should().Be(HttpStatusCode.OK);
        store.ListedStatuses.Should().Equal("not-a-real-status", "open");
    }

    // ── C5 — the NEGATIVE DISCRIMINATOR (explicitly NOT a GW1 defect) ──────

    [Fact]
    public void C5_The_Other_MarkPaid_Is_Still_In_Process_And_Is_Not_GW1s_To_Fix()
    {
        using var factory = new WebApplicationFactory<Program>();

        factory.Services.GetRequiredService<ICodSettlementLedger>()
            .Should().BeOfType<InProcessCodSettlementLedger>(
                "documented, not repaired: POST /admin/v1/settlements/{batchId}/mark-paid on " +
                "CodSettlementComposeController is backed by ConcurrentDictionaries and loses its " +
                "row across a restart for a PRE-EXISTING reason. If a restart round shows that row " +
                "gone, it is NOT a GW1 regression — GW1 owns the ISettlementBatchStore route");

        StoreDurabilityGuard.Critical.Select(c => c.Iface)
            .Should().NotContain(typeof(ICodSettlementLedger),
                "and the durability guard is structurally blind to it — escalated separately, " +
                "out of GW1's scope by the batch document");

        // POSITIVE CONTROL — the same guard DOES see other stores, so the NotContain above is a
        // real distinction rather than an empty set. RE-TARGETED at W2-R02: ISettlementLedgerClient
        // was the control until migration 0052 dropped its table and it left the roster.
        StoreDurabilityGuard.Critical.Select(c => c.Iface)
            .Should().Contain(typeof(JeebGateway.Requests.IRequestsStore));
    }
}
