using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class SavedLocationsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BaseRoute = "/api/users/me/saved-locations";

    private readonly WebApplicationFactory<Program> _factory;

    public SavedLocationsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");
        return client;
    }

    [Fact]
    public async Task List_Returns_Empty_For_New_User()
    {
        var client = ClientFor("sl-list-empty");

        var resp = await client.GetAsync(BaseRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ListResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("sl-list-empty");
        body.Items.Should().BeEmpty();
        body.DefaultId.Should().BeNull();
    }

    [Fact]
    public async Task List_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(BaseRoute);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_Returns_201_And_First_Location_Is_Default()
    {
        var client = ClientFor("sl-create-1");

        var resp = await client.PostAsJsonAsync(BaseRoute, new
        {
            label = "Home",
            address = "12 Rainbow St",
            latitude = 31.95,
            longitude = 35.91
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<LocationResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().NotBeNullOrWhiteSpace();
        body.Label.Should().Be("Home");
        body.Latitude.Should().Be(31.95);
        body.IsDefault.Should().BeTrue("the first saved location is the implicit default (REQ-02)");
    }

    [Fact]
    public async Task Create_Persists_And_Is_Listable()
    {
        var client = ClientFor("sl-persist-1");

        var created = await client.PostAsJsonAsync(BaseRoute, new { label = "Work", latitude = 1.0, longitude = 2.0 });
        var createdBody = await created.Content.ReadFromJsonAsync<LocationResponse>();

        var list = await client.GetAsync(BaseRoute);
        var listBody = await list.Content.ReadFromJsonAsync<ListResponse>();

        listBody!.Items.Should().ContainSingle();
        listBody.Items[0].Id.Should().Be(createdBody!.Id);
        listBody.DefaultId.Should().Be(createdBody.Id);
    }

    [Fact]
    public async Task Create_Out_Of_Range_Latitude_Returns_400()
    {
        var client = ClientFor("sl-bad-lat");

        var resp = await client.PostAsJsonAsync(BaseRoute, new { label = "Bad", latitude = 999.0, longitude = 0.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Missing_Label_Returns_400()
    {
        var client = ClientFor("sl-no-label");

        var resp = await client.PostAsJsonAsync(BaseRoute, new { latitude = 1.0, longitude = 2.0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Setting_New_Default_Clears_Previous_Default()
    {
        var client = ClientFor("sl-default-switch");

        var a = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Home", latitude = 1.0, longitude = 1.0 }))
            .Content.ReadFromJsonAsync<LocationResponse>();
        var b = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Work", latitude = 2.0, longitude = 2.0, isDefault = true }))
            .Content.ReadFromJsonAsync<LocationResponse>();

        var list = await (await client.GetAsync(BaseRoute)).Content.ReadFromJsonAsync<ListResponse>();

        list!.DefaultId.Should().Be(b!.Id);
        list.Items.Single(l => l.Id == a!.Id).IsDefault.Should().BeFalse();
        list.Items.Single(l => l.Id == b.Id).IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Update_Edits_Only_Provided_Fields()
    {
        var client = ClientFor("sl-edit-1");
        var created = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Home", address = "Old", latitude = 1.0, longitude = 1.0 }))
            .Content.ReadFromJsonAsync<LocationResponse>();

        var patch = await client.PatchAsJsonAsync($"{BaseRoute}/{created!.Id}", new { address = "New Address" });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patch.Content.ReadFromJsonAsync<LocationResponse>();
        body!.Address.Should().Be("New Address");
        body.Label.Should().Be("Home", "label was not part of the patch");
        body.Latitude.Should().Be(1.0);
    }

    [Fact]
    public async Task Update_Unknown_Id_Returns_404()
    {
        var client = ClientFor("sl-edit-missing");

        var resp = await client.PatchAsJsonAsync($"{BaseRoute}/does-not-exist", new { label = "X" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Removes_Location_And_Returns_204()
    {
        var client = ClientFor("sl-delete-1");
        var created = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Home", latitude = 1.0, longitude = 1.0 }))
            .Content.ReadFromJsonAsync<LocationResponse>();

        var del = await client.DeleteAsync($"{BaseRoute}/{created!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var get = await client.GetAsync($"{BaseRoute}/{created.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Default_Promotes_Oldest_Remaining()
    {
        var client = ClientFor("sl-delete-default");
        var first = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Home", latitude = 1.0, longitude = 1.0 }))
            .Content.ReadFromJsonAsync<LocationResponse>();
        var second = await (await client.PostAsJsonAsync(BaseRoute, new { label = "Work", latitude = 2.0, longitude = 2.0, isDefault = true }))
            .Content.ReadFromJsonAsync<LocationResponse>();

        // second is default; deleting it should promote the oldest remaining (first).
        await client.DeleteAsync($"{BaseRoute}/{second!.Id}");

        var list = await (await client.GetAsync(BaseRoute)).Content.ReadFromJsonAsync<ListResponse>();
        list!.DefaultId.Should().Be(first!.Id);
        list.Items.Single().IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task Delete_Unknown_Id_Returns_404()
    {
        var client = ClientFor("sl-delete-missing");

        var resp = await client.DeleteAsync($"{BaseRoute}/nope");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Locations_Are_Isolated_Per_User()
    {
        var clientA = ClientFor("sl-iso-a");
        var clientB = ClientFor("sl-iso-b");

        await clientA.PostAsJsonAsync(BaseRoute, new { label = "A-Home", latitude = 1.0, longitude = 1.0 });

        var bList = await (await clientB.GetAsync(BaseRoute)).Content.ReadFromJsonAsync<ListResponse>();
        bList!.Items.Should().BeEmpty("user B must not see user A's saved locations");
    }

    private sealed record ListResponse(
        string UserId,
        LocationResponse[] Items,
        string? DefaultId);

    private sealed record LocationResponse(
        string Id,
        string UserId,
        string Label,
        string? Address,
        double Latitude,
        double Longitude,
        bool IsDefault,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
