using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-041 — dual-role identity and BR-1 enforcement.
///
/// Acceptance criteria covered here:
///   1. User can hold Client + Jeeber roles simultaneously.
///   2. Jeeber endpoints reject users without the Jeeber role.
///   3. Admin endpoints reject non-admin users.
///   4. The role check is consistent across protected routes
///      (RequireRoleAttribute, not open-coded checks).
/// </summary>
public class DualRoleIdentityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DualRoleIdentityTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // BR-1: a single account can hold both Client and Jeeber roles.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Dual_Role_Account_Can_Use_Both_Client_And_Jeeber_Endpoints()
    {
        var userId = $"dual-{Guid.NewGuid()}";
        SeedUser(new UserProfile
        {
            Id = userId,
            Phone = "+96550009999",
            Name = "Dual Role",
            Roles = new List<string> { Roles.Client, Roles.Jeeber },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        // Client endpoint: create a delivery request.
        var clientResp = await client.PostAsJsonAsync("/requests", new
        {
            description = "Same person, wearing the Client hat."
        });
        clientResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Jeeber endpoint: flip availability online — same account, different hat.
        var jeeberResp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "car",
            zone = "amman-downtown"
        });
        jeeberResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Jeeber endpoint rejection.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Jeeber_Endpoint_Returns_403_For_Client_Only_User()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"client-only-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", Roles.Client);

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new
        {
            online = true,
            vehicleType = "car",
            zone = "amman-downtown"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Jeeber_Endpoint_Get_Returns_403_For_User_Without_Jeeber_Role()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"no-role-{Guid.NewGuid()}");
        // No X-User-Roles header at all → empty role set, still authenticated.

        var resp = await client.GetAsync("/jeebers/me/availability");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Jeeber_Endpoint_Returns_401_When_Unauthenticated()
    {
        var client = _factory.CreateClient();

        var resp = await client.PatchAsJsonAsync("/jeebers/me/availability", new { online = false });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Client endpoint rejection.
    // -----------------------------------------------------------------

    [Fact]
    public async Task Client_Endpoint_Returns_403_For_Jeeber_Only_User()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"jeeber-only-{Guid.NewGuid()}");
        client.DefaultRequestHeaders.Add("X-User-Roles", Roles.Jeeber);

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "Jeeber trying to act as a Client"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Admin endpoint rejection — covers the canonical admin surfaces
    // post-conversion to [RequireRole(Roles.Admin)].
    // -----------------------------------------------------------------

    [Theory]
    [InlineData("GET",  "/admin/zones/online-jeebers")]
    [InlineData("GET",  "/admin/prohibited-items")]
    [InlineData("GET",  "/admin/prohibited-items/flagged")]
    [InlineData("GET",  "/admin/users/search?name=anything")]
    public async Task Admin_Endpoints_Return_403_For_Non_Admin(string method, string path)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", $"non-admin-{Guid.NewGuid()}");
        // Holds Client + Jeeber but NOT admin — exhaustively rules out
        // "any authenticated user can hit /admin/**".
        client.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        var req = new HttpRequestMessage(new HttpMethod(method), path);
        var resp = await client.SendAsync(req);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Admin_Endpoint_Returns_401_When_Unauthenticated()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_Endpoint_Accepts_Admin_With_Any_Other_Role_Combo()
    {
        // Admins still need to be people — they may legitimately also
        // carry Client/Jeeber roles. The filter requires admin, not
        // admin-only.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Admin},{Roles.Client}");

        var resp = await client.GetAsync("/admin/zones/online-jeebers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private void SeedUser(UserProfile profile)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(profile);
    }
}
