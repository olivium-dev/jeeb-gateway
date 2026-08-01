using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// b05/GW1 W2.6-reduced — route coverage for <see cref="AdminSettlementsController"/>, one of the
/// two money controllers that had NONE. Counted at <c>origin/main</c> 24b3dd6:
/// <c>git grep -n 'AdminSettlementsController' -- tests/</c> → no hits, exit 1, and
/// <c>git grep -n 'admin/settlements' -- tests/</c> → no hits either, so neither the type nor its
/// route string appeared anywhere in the suite.
///
/// <para><b>Why this controller and not the other mark-paid.</b> There are TWO
/// <c>mark-paid</c> routes in this gateway and they are backed by different stores. This one —
/// <c>POST /v1/admin/settlements/batches/{id:guid}/mark-paid</c> — goes through
/// <see cref="ISettlementBatchStore"/>, which is Postgres-backed in prod and is a Critical store
/// under the durability guard. The other, <c>POST /admin/v1/settlements/{batchId}/mark-paid</c> on
/// <c>CodSettlementComposeController</c>, is backed by <c>InProcessCodSettlementLedger</c> (two
/// ConcurrentDictionaries) and loses its row across a restart for a pre-existing reason GW1 does
/// not own. Scoring GW1 on that route would be scoring it on someone else's defect.</para>
///
/// <para>The store is faked so the assertions are about the CONTROLLER — routing, the guid route
/// constraint, RBAC, the 404 vs 200 split, idempotency pass-through, and the response mapping —
/// and not about Postgres. Whether the row survives a real restart is a live claim and is GW1's
/// V-2 leg on MSI; it is deliberately not simulated here.</para>
/// </summary>
public class AdminSettlementsControllerRouteTests
{
    private const string BatchIdA = "11111111-1111-4111-8111-111111111111";
    private const string MissingBatchId = "99999999-9999-4999-8999-999999999999";

    /// <summary>
    /// Deterministic <see cref="ISettlementBatchStore"/>. Records every MarkPaid call so a test can
    /// tell "the controller reached the store" from "the controller short-circuited".
    /// </summary>
    private sealed class FakeBatchStore : ISettlementBatchStore
    {
        private readonly Dictionary<Guid, SettlementBatch> _batches = new();
        public List<(Guid Batch, string Admin)> MarkPaidCalls { get; } = new();

        public void Seed(SettlementBatch b) => _batches[b.Id] = b;

        public Task<SettlementBatch?> GetByIdAsync(Guid batchId, CancellationToken ct)
            => Task.FromResult(_batches.TryGetValue(batchId, out var b) ? b : null);

        public Task<IReadOnlyList<SettlementBatch>> ListByStatusAsync(string status, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SettlementBatch>>(
                _batches.Values.Where(b => b.Status == status).ToList());

        public Task<SettlementBatch> MarkPaidAsync(Guid batchId, string adminUserId, DateTimeOffset paidAt, CancellationToken ct)
        {
            MarkPaidCalls.Add((batchId, adminUserId));
            var b = _batches[batchId];
            if (b.Status != "paid")
            {
                b.Status = "paid";
                b.PaidAt = paidAt;
                b.PaidBy = adminUserId;
            }
            return Task.FromResult(b);
        }

        public Task<IReadOnlyList<Settlement>> ListUnsettledAsync(int limit, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Settlement>>(Array.Empty<Settlement>());

        public Task MarkBatchProcessedAsync(IReadOnlyList<string> settlementIds, DateTimeOffset at, CancellationToken ct)
            => Task.CompletedTask;

        public Task<SettlementBatch> CreateOrGetBatchAsync(
            string jeeberId, DateOnly periodStart, DateOnly periodEnd,
            IReadOnlyList<Settlement> settlements, CancellationToken ct)
            => throw new NotSupportedException("not exercised by these route tests");
    }

    private static SettlementBatch OpenBatch(string id, string jeeberId = "jeeber-1") => new()
    {
        Id                 = Guid.Parse(id),
        JeeberId           = jeeberId,
        PeriodStart        = new DateOnly(2026, 7, 20),
        PeriodEnd          = new DateOnly(2026, 7, 26),
        TotalGrossUsd      = 120.5000m,
        TotalCommissionUsd = 12.0500m,
        TotalNetUsd        = 108.4500m,
        SettlementCount    = 3,
        Currency           = "USD",
        Status             = "open",
        CreatedAt          = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
        UpdatedAt          = new DateTimeOffset(2026, 7, 27, 0, 0, 0, TimeSpan.Zero),
    };

    private static WebApplicationFactory<Program> FactoryWith(FakeBatchStore store) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureServices(services =>
            {
                services.RemoveAll<ISettlementBatchStore>();
                services.AddSingleton<ISettlementBatchStore>(store);
            }));

    private static HttpClient AdminClient(WebApplicationFactory<Program> f, string adminId = "admin-7")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", adminId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return c;
    }

    // ── RBAC: the capability gate is real ──────────────────────────────────

    [Fact]
    public async Task MarkPaid_Without_Identity_Is_Rejected_And_Never_Reaches_The_Store()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);

        var resp = await factory.CreateClient()
            .PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // The load-bearing half: an anonymous caller must not move money. A status code alone
        // could be produced by a route that ran and then failed for an unrelated reason.
        store.MarkPaidCalls.Should().BeEmpty("an unauthenticated caller must never reach MarkPaidAsync");
    }

    [Fact]
    public async Task MarkPaid_As_NonAdmin_Is_Forbidden_And_Never_Reaches_The_Store()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "jeeber-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");

        var resp = await client.PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        store.MarkPaidCalls.Should().BeEmpty("settlements.manage is admin-only; a jeeber must not mark a batch paid");
    }

    // ── The happy path — the POSITIVE CONTROL for both rejections above ────

    [Fact]
    public async Task MarkPaid_As_Admin_Returns_200_Paid_And_Attributes_The_Admin()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);

        var resp = await AdminClient(factory)
            .PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "without a reachable 200 the 401/403 above are indistinguishable from deny-everything");

        var body = await resp.Content.ReadFromJsonAsync<SettlementBatchResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(Guid.Parse(BatchIdA));
        body.Status.Should().Be("paid");
        body.PaidBy.Should().Be("admin-7", "the audit trail is the point of paid_by");
        body.PaidAt.Should().NotBeNull();
        body.TotalNetUsd.Should().Be(108.4500m, "money must round-trip the mapping unchanged");
        body.SettlementCount.Should().Be(3);

        store.MarkPaidCalls.Should().ContainSingle()
            .Which.Should().Be((Guid.Parse(BatchIdA), "admin-7"));
    }

    [Fact]
    public async Task MarkPaid_Is_Idempotent_A_Second_Call_Still_Returns_200_Paid()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);
        var admin = AdminClient(factory);

        var first = await admin.PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);
        var second = await admin.PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        second.StatusCode.Should().Be(HttpStatusCode.OK, "already-paid is idempotent, not an error");

        var body = await second.Content.ReadFromJsonAsync<SettlementBatchResponse>();
        body!.Status.Should().Be("paid");
        body.PaidBy.Should().Be("admin-7", "a replay must not re-attribute the payment to a later admin");
    }

    // ── Not-found and route-constraint behaviour ───────────────────────────

    [Fact]
    public async Task MarkPaid_Unknown_Batch_Is_404_ProblemDetails_And_Never_Marks_Anything()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);

        var resp = await AdminClient(factory)
            .PostAsync($"/v1/admin/settlements/batches/{MissingBatchId}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("batch-not-found");
        store.MarkPaidCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkPaid_NonGuid_Id_Does_Not_Match_The_Route()
    {
        // The {id:guid} constraint is part of the contract: a non-guid must not fall through to
        // the action and blow up inside it.
        var store = new FakeBatchStore();
        using var factory = FactoryWith(store);

        var resp = await AdminClient(factory)
            .PostAsync("/v1/admin/settlements/batches/not-a-guid/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        store.MarkPaidCalls.Should().BeEmpty();
    }

    // ── Reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetBatch_Returns_The_Batch_For_Admin_And_404_For_An_Unknown_Id()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        using var factory = FactoryWith(store);
        var admin = AdminClient(factory);

        var found = await admin.GetAsync($"/v1/admin/settlements/batches/{BatchIdA}");
        found.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await found.Content.ReadFromJsonAsync<SettlementBatchResponse>();
        body!.JeeberId.Should().Be("jeeber-1");
        body.Status.Should().Be("open");

        var missing = await admin.GetAsync($"/v1/admin/settlements/batches/{MissingBatchId}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListBatches_Filters_By_Status_And_Defaults_To_Open()
    {
        var store = new FakeBatchStore();
        store.Seed(OpenBatch(BatchIdA));
        var paid = OpenBatch("22222222-2222-4222-8222-222222222222", "jeeber-2");
        paid.Status = "paid";
        store.Seed(paid);
        using var factory = FactoryWith(store);
        var admin = AdminClient(factory);

        var defaulted = await admin.GetAsync("/v1/admin/settlements/batches");
        defaulted.StatusCode.Should().Be(HttpStatusCode.OK);
        var openRows = await defaulted.Content.ReadFromJsonAsync<List<SettlementBatchResponse>>();
        openRows.Should().ContainSingle().Which.Id.Should().Be(Guid.Parse(BatchIdA));

        var explicitPaid = await admin.GetAsync("/v1/admin/settlements/batches?status=paid");
        var paidRows = await explicitPaid.Content.ReadFromJsonAsync<List<SettlementBatchResponse>>();
        paidRows.Should().ContainSingle().Which.JeeberId.Should().Be("jeeber-2");
    }

    [Fact]
    public async Task ListBatches_Without_Admin_Role_Is_Forbidden()
    {
        var store = new FakeBatchStore();
        using var factory = FactoryWith(store);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "customer-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync("/v1/admin/settlements/batches");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
