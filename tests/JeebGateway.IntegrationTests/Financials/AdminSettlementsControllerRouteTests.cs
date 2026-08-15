using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Financials;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

/// <summary>
/// Route coverage for <see cref="AdminSettlementsController"/>.
///
/// <para><b>gwdbx W2-R11.</b> Payout batches moved to settlement-service, whose <c>/batches/*</c>
/// surface requires the ADMIN scope. The gateway holds the SERVICE scope only, on purpose: a
/// leaked gateway token must not be able to pay anyone. So these routes now fail closed with a
/// typed 503 that names the new home. What is still worth pinning is that the REFUSAL is layered
/// correctly — an anonymous caller gets 401 and a non-admin gets 403 BEFORE the 503, the guid
/// route constraint still holds, and the 503 is the typed one rather than an incidental fault.</para>
/// </summary>
public class AdminSettlementsControllerRouteTests
{
    private const string BatchIdA = "11111111-1111-4111-8111-111111111111";

    private static HttpClient AdminClient(WebApplicationFactory<Program> f, string adminId = "admin-7")
    {
        var c = f.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", adminId);
        c.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return c;
    }

    // ── RBAC: the capability gate still runs FIRST ─────────────────────────

    [Fact]
    public async Task MarkPaid_Without_Identity_Is_Unauthorized_Not_ServiceUnavailable()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await factory.CreateClient()
            .PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the auth gate must answer before the upstream-scope refusal, or a 503 would mask it");
    }

    [Fact]
    public async Task MarkPaid_As_NonAdmin_Is_Forbidden_Not_ServiceUnavailable()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "jeeber-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "jeeber");

        var resp = await client.PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "settlements.manage is admin-only; a jeeber must not even learn where payout moved");
    }

    // ── The scope refusal — the POSITIVE CONTROL for both rejections above ──

    [Fact]
    public async Task MarkPaid_As_Admin_Is_A_Typed_503_Naming_The_Admin_Scope()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await AdminClient(factory)
            .PostAsync($"/v1/admin/settlements/batches/{BatchIdA}/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "without a reachable admin path the 401/403 above are indistinguishable from deny-everything");
        (await resp.Content.ReadAsStringAsync())
            .Should().Contain(SettlementAdminScopeException.ProblemType,
                "an operator must be told WHERE payout lives, not handed a bare failure");
    }

    [Fact]
    public async Task MarkPaid_NonGuid_Id_Still_Does_Not_Match_The_Route()
    {
        // The {id:guid} constraint is part of the contract: a non-guid must 404 on the route,
        // not fall through to the action.
        using var factory = new WebApplicationFactory<Program>();

        var resp = await AdminClient(factory)
            .PostAsync("/v1/admin/settlements/batches/not-a-guid/mark-paid", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Reads ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Batch_Reads_Are_The_Same_Typed_503_Never_A_Confident_Empty()
    {
        using var factory = new WebApplicationFactory<Program>();
        var admin = AdminClient(factory);

        foreach (var route in new[]
                 {
                     "/v1/admin/settlements/batches",
                     "/v1/admin/settlements/batches?status=paid",
                     $"/v1/admin/settlements/batches/{BatchIdA}",
                 })
        {
            var resp = await admin.GetAsync(route);
            resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
                $"an empty 200 on {route} would tell an operator there are no batches — a claim the gateway cannot make");
            (await resp.Content.ReadAsStringAsync()).Should().Contain(SettlementAdminScopeException.ProblemType);
        }
    }

    [Fact]
    public async Task ListBatches_Without_Admin_Role_Is_Forbidden()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "customer-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var resp = await client.GetAsync("/v1/admin/settlements/batches");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
