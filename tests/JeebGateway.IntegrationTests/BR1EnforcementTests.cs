using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-041 — BR-1 enforcement unit tests.
///
/// BR-1: a user cannot act as both Client and Jeeber simultaneously in
/// the same delivery. This test class covers:
///   1. DualRoleService role-switch validation (active deliveries block).
///   2. DualRoleService same-delivery violation detection.
///   3. POST /users/{id}/switch-role endpoint happy + error paths.
///   4. Offer-accept BR-1 rejection (Jeeber cannot accept own request).
/// </summary>
public class BR1EnforcementTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public BR1EnforcementTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------
    // 1. DualRoleService — ValidateRoleSwitchAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task Switch_Allowed_When_No_Active_Deliveries()
    {
        var userId = $"switch-ok-{Guid.NewGuid()}";
        SeedDualRoleUser(userId);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync(userId, Roles.Jeeber, CancellationToken.None);

        result.IsAllowed.Should().BeTrue();
        result.PreviousRole.Should().Be(Roles.Client);
        result.NewRole.Should().Be(Roles.Jeeber);
    }

    [Fact]
    public async Task Switch_Denied_When_Active_Client_Requests_Exist()
    {
        var userId = $"switch-blocked-client-{Guid.NewGuid()}";
        SeedDualRoleUser(userId);

        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        await store.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "Active delivery blocking switch"
        }, CancellationToken.None);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync(userId, Roles.Jeeber, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("active delivery");
    }

    [Fact]
    public async Task Switch_Denied_When_Active_Jeeber_Deliveries_Exist()
    {
        var userId = $"switch-blocked-jeeber-{Guid.NewGuid()}";
        var clientId = $"client-for-{userId}";
        SeedDualRoleUser(userId, activeRole: Roles.Jeeber);

        var store = _factory.Services.GetRequiredService<IRequestsStore>();
        var request = await store.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Delivery assigned to jeeber"
        }, CancellationToken.None);

        await store.TryAcceptByJeeberAsync(
            request.Id, userId, 5, DateTimeOffset.UtcNow, CancellationToken.None);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync(userId, Roles.Client, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("active delivery");
    }

    [Fact]
    public async Task Switch_Denied_When_User_Missing_Target_Role()
    {
        var userId = $"client-only-switch-{Guid.NewGuid()}";
        SeedUser(new UserProfile
        {
            Id = userId,
            Phone = "+96550001111",
            Name = "Client Only",
            Roles = new List<string> { Roles.Client },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync(userId, Roles.Jeeber, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("does not hold");
    }

    [Fact]
    public async Task Switch_Denied_When_Already_In_Target_Role()
    {
        var userId = $"already-client-{Guid.NewGuid()}";
        SeedDualRoleUser(userId);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync(userId, Roles.Client, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("Already operating");
    }

    [Fact]
    public async Task Switch_Denied_When_User_Not_Found()
    {
        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var result = await service.ValidateRoleSwitchAsync("nonexistent-user", Roles.Jeeber, CancellationToken.None);

        result.IsAllowed.Should().BeFalse();
        result.DenialReason.Should().Contain("not found");
    }

    // -----------------------------------------------------------------
    // 2. DualRoleService — WouldViolateSameDeliveryRuleAsync
    // -----------------------------------------------------------------

    [Fact]
    public async Task Same_Delivery_Violation_When_User_Is_Client()
    {
        var userId = $"same-delivery-client-{Guid.NewGuid()}";
        SeedDualRoleUser(userId);

        var requestStore = _factory.Services.GetRequiredService<IRequestsStore>();
        var request = await requestStore.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "My own delivery"
        }, CancellationToken.None);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var violates = await service.WouldViolateSameDeliveryRuleAsync(userId, request.Id, CancellationToken.None);

        violates.Should().BeTrue();
    }

    [Fact]
    public async Task No_Violation_When_User_Not_Involved_In_Delivery()
    {
        var clientId = $"other-client-{Guid.NewGuid()}";
        var jeeberId = $"unrelated-jeeber-{Guid.NewGuid()}";
        SeedDualRoleUser(jeeberId);

        var requestStore = _factory.Services.GetRequiredService<IRequestsStore>();
        var request = await requestStore.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Someone else's delivery"
        }, CancellationToken.None);

        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var violates = await service.WouldViolateSameDeliveryRuleAsync(jeeberId, request.Id, CancellationToken.None);

        violates.Should().BeFalse();
    }

    [Fact]
    public async Task No_Violation_When_Request_Not_Found()
    {
        var service = _factory.Services.GetRequiredService<IDualRoleService>();
        var violates = await service.WouldViolateSameDeliveryRuleAsync("any-user", "nonexistent-request", CancellationToken.None);

        violates.Should().BeFalse();
    }

    // -----------------------------------------------------------------
    // 3. (Removed) POST /users/{id}/switch-role endpoint tests.
    //
    // The HTTP role-switch surface (RoleSwitchController / UsersRoleController /
    // UsersController) was removed when jeeb-gateway's user-management
    // integration was replaced with the exact salehly-gateway mirror
    // (ServiceUserManagementClient + UserController under /api/User). The
    // shared IDualRoleService validation it enforced is still covered by the
    // unit-level tests in sections 1 & 2 above and the offer-accept BR-1 test
    // in section 4 below.
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // 4. Offer-accept BR-1: Jeeber cannot accept own request
    // -----------------------------------------------------------------

    [Fact]
    public async Task Offer_Accept_Returns_409_When_Jeeber_Is_Client_Of_Same_Delivery()
    {
        var userId = $"self-accept-{Guid.NewGuid()}";
        SeedDualRoleUser(userId, activeRole: Roles.Jeeber);

        var requestStore = _factory.Services.GetRequiredService<IRequestsStore>();
        var request = await requestStore.CreateAsync(new CreateRequestInput
        {
            ClientId = userId,
            Description = "My own delivery I want to accept"
        }, CancellationToken.None);

        var offersStore = _factory.Services.GetRequiredService<InMemoryPendingOffersStore>();
        var offer = offersStore.EnqueueForTest(userId, request.Id);

        var client = CreateAuthenticatedClient(userId, Roles.Jeeber);
        var resp = await client.PostAsync($"/offers/{offer.Id}/accept", null);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("same-delivery-role-violation");
    }

    // -----------------------------------------------------------------
    // 5. (Removed) GET /users/me ActiveRole field test — the /users/me
    //    profile surface lived in the removed UsersController. The
    //    salehly-mirror UserController exposes GET /api/User/profile instead,
    //    proxied to user-management; ActiveRole is no longer a gateway-owned
    //    projection.
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private void SeedDualRoleUser(string userId, string activeRole = "customer")
    {
        SeedUser(new UserProfile
        {
            Id = userId,
            Phone = "+96550009999",
            Name = "Dual-Role Test",
            Roles = new List<string> { Roles.Client, Roles.Jeeber },
            ActiveRole = activeRole,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    private void SeedUser(UserProfile profile)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(profile);
    }

    private HttpClient CreateAuthenticatedClient(string userId, params string[] roles)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", string.Join(",", roles));
        return client;
    }
}
