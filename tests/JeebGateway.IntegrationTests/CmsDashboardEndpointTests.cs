using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.IntegrationTests.Financials;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// D2 — <c>GET /cms-admin/v1/dashboard/summary</c>, the route the deployed back-office shell
/// calls (it 404'd, so the whole dashboard rendered its error page).
///
/// <para>These pin the three things a CMS bundle we cannot rebuild depends on: the AUTH answer
/// (401 vs 403), the exact response SHAPE (camelCase keys, PascalCase OrderStatus values, the
/// Money envelope), and the fail-soft contract — one dead data source must degrade ONE tile,
/// never turn the dashboard into a 500.</para>
/// </summary>
public sealed class CmsDashboardEndpointTests
{
    private const string Route = "/cms-admin/v1/dashboard/summary";

    [Fact]
    public async Task Summary_Without_Identity_Is_401()
    {
        using var factory = new WebApplicationFactory<Program>();

        var resp = await factory.CreateClient().GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Summary_For_A_Non_Admin_Is_403_Never_200()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "some-client");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the dashboard reads platform-wide earnings — a client must never see it");
    }

    [Fact]
    public async Task Summary_For_Admin_Returns_The_Full_Contract_Shape_With_Real_Counts()
    {
        var settlements = new FakeSettlementServiceClient();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISettlementServiceClient>();
                services.AddSingleton<ISettlementServiceClient>(settlements);
            }));

        SeedUser(factory, "u-jeeber", "Rami Jeeber", "+96170000001", Roles.Jeeber);
        SeedUser(factory, "u-client", "Lina Client", "+96170000002", Roles.Client);

        await SeedRequestAsync(factory, "u-client", "Pharmacy run", RequestStatus.HeadingOff);
        await SeedRequestAsync(factory, "u-client", "Documents", RequestStatus.AtDoor);
        await SeedRequestAsync(factory, "u-client", "Groceries", RequestStatus.Disputed);
        await SeedRequestAsync(factory, "u-client", "Fresh order", status: null);
        SeedSettlement(settlements, "u-jeeber", goodsCost: 40m);

        var resp = await AdminClient(factory).GetAsync(Route);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await ReadJsonAsync(resp);
        var kpis = body.GetProperty("kpis");

        kpis.GetProperty("ordersTotal").GetInt32().Should().Be(4);
        kpis.GetProperty("ordersInTransit").GetInt32().Should().Be(2, "InTransit + AtDoor");
        kpis.GetProperty("ordersNeedingEscalation").GetInt32().Should().Be(1);
        kpis.GetProperty("usersTotal").GetInt32().Should().BeGreaterThanOrEqualTo(2);
        kpis.GetProperty("jeebersTotal").GetInt32().Should().Be(1);
        kpis.GetProperty("clientsTotal").GetInt32().Should().Be(1);
        kpis.GetProperty("kycPending").GetInt32().Should().Be(0);

        var earnings = kpis.GetProperty("earningsTotal");
        earnings.GetProperty("value").GetDecimal().Should().Be(36m, "net = goodsCost - flat 10% commission");
        earnings.GetProperty("currency").GetString().Should().Be("USD");

        var activity = body.GetProperty("recentActivity").EnumerateArray().ToList();
        activity.Should().HaveCount(4);
        activity.Should().OnlyContain(i =>
            i.GetProperty("id").GetString() != null
            && i.GetProperty("title").GetString() != null
            && i.GetProperty("updatedAt").GetString() != null);

        var statuses = activity.Select(i => i.GetProperty("status").GetString()).ToList();
        statuses.Should().BeSubsetOf(new[]
        {
            "Ordered", "Picked", "InTransit", "AtDoor", "Done", "Cancelled", "FailedNeedsEscalation"
        }, "the CMS binds a PascalCase OrderStatus enum verbatim");
        statuses.Should().Contain("InTransit").And.Contain("AtDoor").And.Contain("FailedNeedsEscalation");
        statuses.Should().Contain("Ordered", "a fresh pending request reads as Ordered");

        activity.Should().Contain(i => i.GetProperty("clientName").GetString() == "Lina Client");
    }

    [Fact]
    public async Task Summary_Stays_200_With_One_Dead_Source_And_Only_That_Tile_Degrades()
    {
        // settlement-service is the fail-soft probe: it throws on every read, so the earnings
        // tile must zero while every other tile still reports its real number.
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ISettlementServiceClient>();
                services.AddSingleton<ISettlementServiceClient>(
                    FakeSettlementServiceClient.Unreachable());
            }));

        SeedUser(factory, "u-jeeber", "Rami Jeeber", "+96170000001", Roles.Jeeber);
        await SeedRequestAsync(factory, "u-client", "Pharmacy run", RequestStatus.HeadingOff);

        var resp = await AdminClient(factory).GetAsync(Route);

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "one degraded source must zero ONE tile, never 500 the dashboard");

        var kpis = (await ReadJsonAsync(resp)).GetProperty("kpis");
        kpis.GetProperty("earningsTotal").GetProperty("value").GetDecimal().Should().Be(0m);
        kpis.GetProperty("ordersTotal").GetInt32().Should().Be(1, "the healthy widgets are unaffected");
        kpis.GetProperty("jeebersTotal").GetInt32().Should().Be(1);
    }

    // ----- helpers -----------------------------------------------------------

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-admin");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static void SeedUser(
        WebApplicationFactory<Program> factory, string id, string name, string phone, string role)
        => factory.Services.GetRequiredService<InMemoryUsersStore>().Seed(new UserProfile
        {
            Id = id,
            Phone = phone,
            Name = name,
            Language = "en",
            Roles = new List<string> { role },
            ActiveRole = role,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

    private static async Task SeedRequestAsync(
        WebApplicationFactory<Program> factory, string clientId, string description, string? status)
    {
        var store = factory.Services.GetRequiredService<IRequestsStore>();
        var row = await store.CreateAsync(
            new CreateRequestInput { ClientId = clientId, Description = description }, default);
        if (status is not null) await store.SetStatusAsync(row.Id, status, default);
    }

    // Commission is computed by settlement-service (flat 10%); the test double does the same.
    private static void SeedSettlement(
        FakeSettlementServiceClient settlements, string jeeberId, decimal goodsCost)
        => settlements.SettleAsync(new SettlementSettleCommand(
            DeliveryId: Guid.NewGuid().ToString(),
            HolderId: jeeberId,
            ClientId: "u-client",
            TierId: "standard",
            GrossAmount: goodsCost,
            PaymentMethod: "cash"), default).GetAwaiter().GetResult();

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        using var doc = await JsonDocument.ParseAsync(await resp.Content.ReadAsStreamAsync());
        return doc.RootElement.Clone();
    }

}
