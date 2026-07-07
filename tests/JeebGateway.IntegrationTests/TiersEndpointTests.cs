using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Tiers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-009: delivery tier catalog endpoints. Asserts both the public
/// read surface (GET /tiers returns the three seeded tiers with the required
/// shape) and the admin CRUD surface (POST/PUT/DELETE /admin/tiers), plus
/// the "tier changes take effect on next request" acceptance criterion.
///
/// Each test uses a *fresh* WebApplicationFactory so the InMemoryTiersStore
/// starts from its seeded state — different tests must not race on the
/// singleton catalog.
/// </summary>
public class TiersEndpointTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    // -----------------------------------------------------------------
    // Public GET /tiers
    // -----------------------------------------------------------------

    [Fact]
    public async Task Get_Tiers_Returns_Three_Default_Tiers_With_Required_Fields()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/tiers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ListResponse>();
        body!.Items.Should().HaveCount(3);

        body.Items.Select(t => t.Name).Should().BeEquivalentTo(new[]
        {
            "Urgent", "Same-Day", "Scheduled"
        });

        body.Items.Single(t => t.Id == "urgent").RadiusKm.Should().Be(3.0);
        body.Items.Single(t => t.Id == "urgent").RequestTtlSeconds.Should().Be(30 * 60);
        body.Items.Single(t => t.Id == "same-day").RadiusKm.Should().Be(10.0);
        body.Items.Single(t => t.Id == "same-day").RequestTtlSeconds.Should().Be(2 * 60 * 60);
        body.Items.Single(t => t.Id == "scheduled").RadiusKm.Should().Be(25.0);
        body.Items.Single(t => t.Id == "scheduled").RequestTtlSeconds.Should().Be(24 * 60 * 60);

        foreach (var t in body.Items)
        {
            t.Id.Should().NotBeNullOrWhiteSpace();
            t.SlaHours.Should().BeGreaterThan(0);
            t.RadiusKm.Should().BeGreaterThan(0);
            t.RequestTtlSeconds.Should().BeGreaterThan(0);
            t.CommissionRate.Should().BeInRange(0, 1);
            t.PriceHint.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public async Task Get_Tiers_Does_Not_Require_Authentication()
    {
        using var factory = NewFactory();
        var anon = factory.CreateClient();

        var resp = await anon.GetAsync("/tiers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Admin CRUD
    // -----------------------------------------------------------------

    [Fact]
    public async Task Admin_Create_Returns_201_With_Generated_Slug_Id()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-create-1");

        var resp = await admin.PostAsJsonAsync("/admin/tiers", new
        {
            name = "Overnight",
            slaHours =12,
            radiusKm =20.0,
            requestTtlSeconds =12 * 60 * 60,
            commissionRate =0.22,
            priceHint ="Pick up tonight, drop off tomorrow"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await resp.Content.ReadFromJsonAsync<TierDto>();
        created!.Id.Should().Be("overnight");
        created.Name.Should().Be("Overnight");
        created.SlaHours.Should().Be(12);
        created.RadiusKm.Should().Be(20.0);
        created.RequestTtlSeconds.Should().Be(12 * 60 * 60);
        created.CommissionRate.Should().Be(0.22);

        var list = await admin.GetFromJsonAsync<ListResponse>("/tiers");
        list!.Items.Should().Contain(t => t.Id == "overnight");
    }

    [Fact]
    public async Task Admin_Create_Without_Admin_Role_Returns_403()
    {
        using var factory = NewFactory();
        var client = ClientFor(factory, "not-admin");

        var resp = await client.PostAsJsonAsync("/admin/tiers", new
        {
            name = "ShouldFail",
            slaHours =1,
            radiusKm =1.0,
            requestTtlSeconds =30 * 60,
            commissionRate =0.1,
            priceHint ="x"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_Create_Without_Identity_Returns_401()
    {
        using var factory = NewFactory();
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/admin/tiers", new { name = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("", 1, 1.0, 0.1, "x", "blank name")]
    [InlineData("X", 0, 1.0, 0.1, "x", "sla zero")]
    [InlineData("X", 1, 0.0, 0.1, "x", "radius zero")]
    [InlineData("X", 1, 1.0, -0.1, "x", "negative commission")]
    [InlineData("X", 1, 1.0, 1.5, "x", "commission > 1")]
    [InlineData("X", 1, 1.0, 0.1, "", "blank price hint")]
    public async Task Admin_Create_Rejects_Invalid_Input(
        string name, int sla, double radius, double commission, string hint, string reason)
    {
        _ = reason;
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-bad-input");

        var resp = await admin.PostAsJsonAsync("/admin/tiers", new
        {
            name,
            slaHours =sla,
            radiusKm =radius,
            requestTtlSeconds =30 * 60,
            commissionRate =commission,
            priceHint =hint
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Create_Returns_409_On_Case_Insensitive_Duplicate_Name()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-dup");

        var resp = await admin.PostAsJsonAsync("/admin/tiers", new
        {
            name = "urgent",
            slaHours =2,
            radiusKm =5.0,
            requestTtlSeconds =30 * 60,
            commissionRate =0.3,
            priceHint ="duplicate"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Admin_Create_Rejects_Invalid_Custom_Id_Slug()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-bad-id");

        var resp = await admin.PostAsJsonAsync("/admin/tiers", new
        {
            id = "Not A Slug!",
            name = "BadSlug",
            slaHours =1,
            radiusKm =1.0,
            requestTtlSeconds =30 * 60,
            commissionRate =0.1,
            priceHint ="x"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Put_Replaces_Tier_Fields()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-put");

        var resp = await admin.PutAsJsonAsync("/admin/tiers/urgent", new
        {
            name = "Urgent",
            slaHours =2,
            radiusKm =7.5,
            requestTtlSeconds =45 * 60,
            commissionRate =0.30,
            priceHint ="Updated hint"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await resp.Content.ReadFromJsonAsync<TierDto>();
        updated!.SlaHours.Should().Be(2);
        updated.RadiusKm.Should().Be(7.5);
        updated.RequestTtlSeconds.Should().Be(45 * 60);
        updated.CommissionRate.Should().Be(0.30);
        updated.PriceHint.Should().Be("Updated hint");
    }

    [Fact]
    public async Task Admin_Put_Of_Missing_Tier_Returns_404()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-put-404");

        var resp = await admin.PutAsJsonAsync("/admin/tiers/does-not-exist", new
        {
            name = "Ghost",
            slaHours =1,
            radiusKm =1.0,
            requestTtlSeconds =30 * 60,
            commissionRate =0.1,
            priceHint ="x"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_Delete_Removes_Tier_And_Subsequent_Get_Excludes_It()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-delete");

        var del = await admin.DeleteAsync("/admin/tiers/scheduled");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await admin.GetFromJsonAsync<ListResponse>("/tiers");
        list!.Items.Should().NotContain(t => t.Id == "scheduled");
        list.Items.Should().HaveCount(2);
    }

    [Fact]
    public async Task Admin_Delete_Of_Missing_Tier_Returns_404()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-del-404");

        var resp = await admin.DeleteAsync("/admin/tiers/does-not-exist");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // Acceptance criterion: tier changes take effect on the next request.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Tier_Update_Is_Visible_On_Next_Request()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory, "admin-next");
        var reader = factory.CreateClient();

        var before = await reader.GetFromJsonAsync<ListResponse>("/tiers");
        var urgentBefore = before!.Items.Single(t => t.Id == "urgent");
        urgentBefore.SlaHours.Should().Be(1);

        var put = await admin.PutAsJsonAsync("/admin/tiers/urgent", new
        {
            name = "Urgent",
            slaHours =3,
            radiusKm =6.0,
            requestTtlSeconds =60 * 60,
            commissionRate =0.27,
            priceHint ="Now slower"
        });
        put.EnsureSuccessStatusCode();

        var after = await reader.GetFromJsonAsync<ListResponse>("/tiers");
        var urgentAfter = after!.Items.Single(t => t.Id == "urgent");
        urgentAfter.SlaHours.Should().Be(3);
        urgentAfter.RadiusKm.Should().Be(6.0);
        urgentAfter.RequestTtlSeconds.Should().Be(60 * 60);
    }

    [Fact]
    public async Task Snapshot_Returned_By_List_Is_Decoupled_From_Subsequent_Mutations()
    {
        // The contract is "changes take effect on next request, not retroactively"
        // — i.e. a snapshot already returned must not mutate in place. The store
        // is verified directly here so the assertion does not depend on HTTP
        // serialization caching.
        var store = new InMemoryTiersStore();
        var snapshot = await store.ListAsync(CancellationToken.None);
        var snapshotUrgent = snapshot.Single(t => t.Id == "urgent");
        var originalSla = snapshotUrgent.SlaHours;

        await store.ReplaceAsync("urgent", new DeliveryTierReplace
        {
            Name = "Urgent",
            SlaHours = originalSla + 100,
            RadiusKm = 99.0,
            RequestTtlSeconds = 90 * 60,
            CommissionRate = 0.42,
            PriceHint = "mutated"
        }, "admin", CancellationToken.None);

        snapshotUrgent.SlaHours.Should().Be(originalSla);

        var fresh = await store.ListAsync(CancellationToken.None);
        fresh.Single(t => t.Id == "urgent").SlaHours.Should().Be(originalSla + 100);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = ClientFor(factory, userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private sealed record TierDto(
        string Id,
        string Name,
        int SlaHours,
        double RadiusKm,
        int RequestTtlSeconds,
        double CommissionRate,
        string PriceHint,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record ListResponse(TierDto[] Items);
}
