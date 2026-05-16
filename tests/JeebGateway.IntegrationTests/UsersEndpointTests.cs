using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class UsersEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UsersEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetMe_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_Bootstraps_Profile_On_First_Call()
    {
        var client = ClientFor("user-bootstrap");

        var resp = await client.GetAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UserProfileResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be("user-bootstrap");
        body.Language.Should().Be("en");
        body.Roles.Should().Contain("customer");
        body.SavedAddresses.Should().BeEmpty();
        // BR-10 / T-backend-039: a never-rated user surfaces IsNew=true
        // so the mobile app renders the "New" badge in place of a rating.
        body.IsNew.Should().BeTrue();
        body.Rating.Should().BeNull();
        body.RatingCount.Should().Be(0);
    }

    [Fact]
    public async Task GetMe_Returns_Seeded_Roles_And_Rating()
    {
        SeedUser(new UserProfile
        {
            Id = "user-seeded",
            Phone = "+96550001234",
            Name = "Layla",
            Email = "layla@example.com",
            Roles = new List<string> { "customer", "driver" },
            Rating = 4.75m,
            RatingCount = 41,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-30),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-30)
        });
        var client = ClientFor("user-seeded");

        var resp = await client.GetAsync("/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UserProfileResponse>();
        body!.Name.Should().Be("Layla");
        body.Phone.Should().Be("+96550001234");
        body.Roles.Should().BeEquivalentTo(new[] { "customer", "driver" });
        body.Rating.Should().Be(4.75m);
        body.RatingCount.Should().Be(41);
        // Once a user has any ratings, the "New" badge goes away.
        body.IsNew.Should().BeFalse();
    }

    [Fact]
    public async Task PatchMe_Updates_Only_Provided_Fields()
    {
        var client = ClientFor("user-patch");

        var resp = await client.PatchAsJsonAsync("/users/me", new
        {
            name = "Yara",
            language = "ar",
            avatarUrl = "https://cdn.example.com/y.png"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UserProfileResponse>();
        body!.Name.Should().Be("Yara");
        body.Language.Should().Be("ar");
        body.AvatarUrl.Should().Be("https://cdn.example.com/y.png");
    }

    [Fact]
    public async Task PatchMe_Rejects_Invalid_Language()
    {
        var client = ClientFor("user-bad-lang");

        var resp = await client.PatchAsJsonAsync("/users/me", new { language = "english" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PatchMe_Rejects_Blank_Name()
    {
        var client = ClientFor("user-blank-name");

        var resp = await client.PatchAsJsonAsync("/users/me", new { name = "   " });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAddress_Then_List_Returns_Item()
    {
        var client = ClientFor("user-addr-1");

        var create = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 4, Street 12",
            city = "Kuwait City",
            country = "KW",
            latitude = 29.3759m,
            longitude = 47.9774m,
            isDefault = true
        });
        create.StatusCode.Should().Be(HttpStatusCode.Created);

        var list = await client.GetFromJsonAsync<List<SavedAddressResponse>>("/users/me/addresses");
        list.Should().HaveCount(1);
        list![0].Label.Should().Be("Home");
        list[0].IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task CreateAddress_Without_Required_Fields_Returns_400()
    {
        var client = ClientFor("user-addr-400");

        var resp = await client.PostAsJsonAsync("/users/me/addresses", new { label = "Home" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateAddress_With_Duplicate_Label_Returns_409()
    {
        var client = ClientFor("user-addr-dup");

        var first = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 4"
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "HOME",
            line1 = "Block 5"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Setting_Default_Address_Unsets_Other_Defaults()
    {
        var client = ClientFor("user-addr-default");

        var home = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 1",
            isDefault = true
        });
        home.EnsureSuccessStatusCode();
        var homeAddr = await home.Content.ReadFromJsonAsync<SavedAddressResponse>();

        var office = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Office",
            line1 = "Tower X",
            isDefault = true
        });
        office.EnsureSuccessStatusCode();

        var list = await client.GetFromJsonAsync<List<SavedAddressResponse>>("/users/me/addresses");
        var refreshedHome = list!.Single(a => a.Id == homeAddr!.Id);
        refreshedHome.IsDefault.Should().BeFalse();
        list!.Single(a => a.Label == "Office").IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task PatchAddress_Updates_Fields()
    {
        var client = ClientFor("user-addr-patch");

        var created = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 1"
        });
        var addr = await created.Content.ReadFromJsonAsync<SavedAddressResponse>();

        var patch = await client.PatchAsJsonAsync($"/users/me/addresses/{addr!.Id}", new
        {
            line1 = "Block 2",
            line2 = "Building A"
        });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await patch.Content.ReadFromJsonAsync<SavedAddressResponse>();
        updated!.Line1.Should().Be("Block 2");
        updated.Line2.Should().Be("Building A");
        updated.Label.Should().Be("Home");
    }

    [Fact]
    public async Task DeleteAddress_Returns_204_Then_404()
    {
        var client = ClientFor("user-addr-delete");

        var created = await client.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 1"
        });
        var addr = await created.Content.ReadFromJsonAsync<SavedAddressResponse>();

        var del = await client.DeleteAsync($"/users/me/addresses/{addr!.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var again = await client.DeleteAsync($"/users/me/addresses/{addr.Id}");
        again.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Addresses_Are_Scoped_To_The_Owning_User()
    {
        var owner = ClientFor("user-addr-iso-a");
        var stranger = ClientFor("user-addr-iso-b");

        var created = await owner.PostAsJsonAsync("/users/me/addresses", new
        {
            label = "Home",
            line1 = "Block 1"
        });
        var addr = await created.Content.ReadFromJsonAsync<SavedAddressResponse>();

        var get = await stranger.GetAsync($"/users/me/addresses/{addr!.Id}");
        get.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var del = await stranger.DeleteAsync($"/users/me/addresses/{addr.Id}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AdminSearch_Without_Admin_Role_Returns_403()
    {
        var client = ClientFor("user-not-admin");

        var resp = await client.GetAsync("/admin/users/search?name=anyone");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AdminSearch_Without_Any_Filter_Returns_400()
    {
        var client = AdminClient("user-admin-1");

        var resp = await client.GetAsync("/admin/users/search");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AdminSearch_By_Name_Returns_Matching_Users_With_Pagination()
    {
        SeedUser(new UserProfile
        {
            Id = "search-1",
            Phone = "+96550009001",
            Name = "Ahmad Hassan",
            Roles = new List<string> { "customer" },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-2)
        });
        SeedUser(new UserProfile
        {
            Id = "search-2",
            Phone = "+96550009002",
            Name = "Ahmad Khaled",
            Roles = new List<string> { "customer", "driver" },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });

        var client = AdminClient("user-admin-2");

        var resp = await client.GetAsync("/admin/users/search?name=ahmad&page=1&pageSize=10");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminUserSearchResponse>();
        body!.Items.Should().Contain(i => i.Id == "search-1");
        body.Items.Should().Contain(i => i.Id == "search-2");
        body.Page.Should().Be(1);
        body.PageSize.Should().Be(10);
        body.Total.Should().BeGreaterOrEqualTo(2);
    }

    [Fact]
    public async Task AdminSearch_By_Phone_Or_Email_Filters()
    {
        SeedUser(new UserProfile
        {
            Id = "search-phone-1",
            Phone = "+96550007777",
            Name = "Nour",
            Email = "nour@example.com",
            Roles = new List<string> { "customer" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var client = AdminClient("user-admin-3");

        var byPhone = await client.GetFromJsonAsync<AdminUserSearchResponse>("/admin/users/search?phone=07777");
        byPhone!.Items.Should().Contain(i => i.Id == "search-phone-1");

        var byEmail = await client.GetFromJsonAsync<AdminUserSearchResponse>("/admin/users/search?email=nour@");
        byEmail!.Items.Should().Contain(i => i.Id == "search-phone-1");
    }

    [Fact]
    public async Task AdminSearch_Rejects_Invalid_Page_Size()
    {
        var client = AdminClient("user-admin-4");

        var resp = await client.GetAsync("/admin/users/search?name=x&pageSize=999");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------
    // Helpers
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

    private void SeedUser(UserProfile profile)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(profile);
    }
}
