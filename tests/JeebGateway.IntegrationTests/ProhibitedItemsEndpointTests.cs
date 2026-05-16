using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class ProhibitedItemsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ProhibitedItemsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // Admin CRUD
    // -----------------------------------------------------------------

    [Fact]
    public async Task Admin_Create_Returns_201_With_Id_And_Active_True()
    {
        var admin = AdminClient("admin-create-1");

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "Weapon-Test-1",
            category = "weapons",
            description = "Firearms and ammunition"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<ItemDto>();
        body!.Id.Should().NotBeNullOrWhiteSpace();
        body.Name.Should().Be("Weapon-Test-1");
        body.Category.Should().Be("weapons");
        body.Active.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_Create_Without_Admin_Role_Returns_403()
    {
        var client = ClientFor("user-not-admin");

        var resp = await client.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "ShouldNotCreate",
            category = "weapons"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_Create_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "Anon",
            category = "weapons"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_Create_Rejects_Blank_Name()
    {
        var admin = AdminClient("admin-blank-name");

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "   ",
            category = "weapons"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Create_Rejects_Invalid_Category_Slug()
    {
        var admin = AdminClient("admin-bad-cat");

        var resp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "BadCat-Test",
            category = "Weapons!" // uppercase + punctuation
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Admin_Create_Returns_409_On_Case_Insensitive_Duplicate_Name()
    {
        var admin = AdminClient("admin-dup-1");

        var first = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "DupItem-X",
            category = "weapons"
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "dupitem-x",
            category = "weapons"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Admin_Update_Edits_Name_And_Category()
    {
        var admin = AdminClient("admin-edit-1");

        var created = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "EditMe-Original",
            category = "weapons"
        });
        var item = await created.Content.ReadFromJsonAsync<ItemDto>();

        var patch = await admin.PatchAsJsonAsync($"/admin/prohibited-items/{item!.Id}", new
        {
            name = "EditMe-Renamed",
            category = "hazardous_materials"
        });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await patch.Content.ReadFromJsonAsync<ItemDto>();
        updated!.Name.Should().Be("EditMe-Renamed");
        updated.Category.Should().Be("hazardous_materials");
        updated.Active.Should().BeTrue();
    }

    [Fact]
    public async Task Admin_Update_Can_Deactivate_Item()
    {
        var admin = AdminClient("admin-deact-1");

        var created = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "Deactivate-Me",
            category = "weapons"
        });
        var item = await created.Content.ReadFromJsonAsync<ItemDto>();

        var patch = await admin.PatchAsJsonAsync($"/admin/prohibited-items/{item!.Id}", new
        {
            active = false
        });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await patch.Content.ReadFromJsonAsync<ItemDto>();
        updated!.Active.Should().BeFalse();

        var mobile = ClientFor("mobile-after-deact");
        var listResp = await mobile.GetAsync("/prohibited-items");
        var list = await listResp.Content.ReadFromJsonAsync<ListResponse>();
        list!.Items.Should().NotContain(i => i.Id == item.Id);
    }

    [Fact]
    public async Task Admin_Update_Of_Missing_Item_Returns_404()
    {
        var admin = AdminClient("admin-missing");

        var patch = await admin.PatchAsJsonAsync(
            $"/admin/prohibited-items/{Guid.NewGuid()}",
            new { active = false });

        patch.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Admin_List_Paginates_And_Includes_Inactive()
    {
        var admin = AdminClient("admin-list-1");

        var created = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "ListItem-Inactive",
            category = "weapons"
        });
        var item = await created.Content.ReadFromJsonAsync<ItemDto>();
        await admin.PatchAsJsonAsync($"/admin/prohibited-items/{item!.Id}", new { active = false });

        var listResp = await admin.GetAsync("/admin/prohibited-items?page=1&pageSize=100");
        listResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await listResp.Content.ReadFromJsonAsync<AdminListResponse>();
        body!.Items.Should().Contain(i => i.Id == item.Id && i.Active == false);
    }

    [Fact]
    public async Task Admin_Get_Returns_Item()
    {
        var admin = AdminClient("admin-get-1");

        var created = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "GetItem-1",
            category = "weapons"
        });
        var item = await created.Content.ReadFromJsonAsync<ItemDto>();

        var resp = await admin.GetAsync($"/admin/prohibited-items/{item!.Id}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var got = await resp.Content.ReadFromJsonAsync<ItemDto>();
        got!.Name.Should().Be("GetItem-1");
    }

    // -----------------------------------------------------------------
    // Mobile read + acknowledge
    // -----------------------------------------------------------------

    [Fact]
    public async Task Mobile_List_Returns_Only_Active_Items()
    {
        var admin = AdminClient("admin-mobile-prep-1");
        var activeResp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "MobileActive-1",
            category = "weapons"
        });
        var active = await activeResp.Content.ReadFromJsonAsync<ItemDto>();

        var inactiveResp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "MobileInactive-1",
            category = "weapons"
        });
        var inactive = await inactiveResp.Content.ReadFromJsonAsync<ItemDto>();
        await admin.PatchAsJsonAsync($"/admin/prohibited-items/{inactive!.Id}", new { active = false });

        var mobile = ClientFor("mobile-list-1");
        var resp = await mobile.GetAsync("/prohibited-items");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ListResponse>();
        body!.Items.Should().Contain(i => i.Id == active!.Id);
        body.Items.Should().NotContain(i => i.Id == inactive.Id);
        body.Version.Should().NotBeNullOrWhiteSpace();
        body.Acknowledged.Should().BeFalse();
        body.AcknowledgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Mobile_List_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/prohibited-items");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mobile_Acknowledge_Records_And_Subsequent_Get_Reflects_It()
    {
        var admin = AdminClient("admin-ack-prep");
        var createResp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "AckItem-1",
            category = "weapons"
        });
        createResp.EnsureSuccessStatusCode();

        var mobile = ClientFor("mobile-ack-1");
        var listResp = await mobile.GetAsync("/prohibited-items");
        var list = await listResp.Content.ReadFromJsonAsync<ListResponse>();
        list!.Acknowledged.Should().BeFalse();

        var ack = await mobile.PostAsJsonAsync("/prohibited-items/acknowledge", new
        {
            version = list.Version
        });
        ack.StatusCode.Should().Be(HttpStatusCode.OK);
        var ackBody = await ack.Content.ReadFromJsonAsync<AckResponse>();
        ackBody!.UserId.Should().Be("mobile-ack-1");
        ackBody.Version.Should().Be(list.Version);

        var listAgain = await mobile.GetAsync("/prohibited-items");
        var listAgainBody = await listAgain.Content.ReadFromJsonAsync<ListResponse>();
        listAgainBody!.Acknowledged.Should().BeTrue();
        listAgainBody.AcknowledgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Mobile_Acknowledge_With_Stale_Version_Returns_409()
    {
        var admin = AdminClient("admin-stale-prep");
        await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "StaleAck-1",
            category = "weapons"
        });

        var mobile = ClientFor("mobile-stale-1");

        var resp = await mobile.PostAsJsonAsync("/prohibited-items/acknowledge", new
        {
            version = "1999-01-01T00:00:00.0000000+00:00"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Mobile_Acknowledge_Requires_Version()
    {
        var mobile = ClientFor("mobile-no-version");

        var resp = await mobile.PostAsJsonAsync("/prohibited-items/acknowledge", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Acknowledgment_Goes_Stale_When_Admin_Edits_Catalog()
    {
        var admin = AdminClient("admin-bump-prep");
        var createResp = await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "BumpItem-1",
            category = "weapons"
        });
        var item = await createResp.Content.ReadFromJsonAsync<ItemDto>();

        var mobile = ClientFor("mobile-bump-1");
        var before = await mobile.GetFromJsonAsync<ListResponse>("/prohibited-items");
        await mobile.PostAsJsonAsync("/prohibited-items/acknowledge", new { version = before!.Version });

        await Task.Delay(10);
        await admin.PatchAsJsonAsync($"/admin/prohibited-items/{item!.Id}", new
        {
            description = "Updated description"
        });

        var after = await mobile.GetFromJsonAsync<ListResponse>("/prohibited-items");
        after!.Version.Should().NotBe(before.Version);
        after.Acknowledged.Should().BeFalse();
    }

    [Fact]
    public async Task Acknowledgments_Are_Isolated_Per_User()
    {
        var admin = AdminClient("admin-iso-prep");
        await admin.PostAsJsonAsync("/admin/prohibited-items", new
        {
            name = "IsoItem-1",
            category = "weapons"
        });

        var a = ClientFor("mobile-iso-a");
        var b = ClientFor("mobile-iso-b");

        var listA = await a.GetFromJsonAsync<ListResponse>("/prohibited-items");
        await a.PostAsJsonAsync("/prohibited-items/acknowledge", new { version = listA!.Version });

        var listB = await b.GetFromJsonAsync<ListResponse>("/prohibited-items");
        listB!.Acknowledged.Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // Helpers / DTOs
    // -----------------------------------------------------------------

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    private HttpClient AdminClient(string userId)
    {
        var client = ClientFor(userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private sealed record ItemDto(
        string Id,
        string Name,
        string Category,
        string? Description,
        bool Active,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record AdminListResponse(
        ItemDto[] Items,
        int Page,
        int PageSize,
        int Total);

    private sealed record ListResponse(
        ItemDto[] Items,
        string Version,
        bool Acknowledged,
        DateTimeOffset? AcknowledgedAt);

    private sealed record AckResponse(
        string UserId,
        string Version,
        DateTimeOffset AcknowledgedAt);
}
