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
// GW3 / W3.5(c): the class fixture is now FakeOfferStoreWebApplicationFactory, not a bare
// WebApplicationFactory<Program>. Program.cs used to register an in-memory offer store and
// select it whenever FeatureFlags:UseUpstream:Offer was false, so a bare factory silently
// handed this class a working offer ledger. The gateway ships none now — offer-service is
// the ledger of record — so the fixture supplies the test-owned double explicitly.
public class BR1EnforcementTests : IClassFixture<Fakes.FakeOfferStoreWebApplicationFactory>
{
    private readonly Fakes.FakeOfferStoreWebApplicationFactory _factory;

    public BR1EnforcementTests(Fakes.FakeOfferStoreWebApplicationFactory factory)
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
            Phone = "+9613001111",
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
    // 4. (Removed 2026-08-01) Offer-accept BR-1 self-offer 409.
    //
    //    This drove the retired POST /offers/{id}/accept route, which the owner
    //    retired as a duplicate of POST /v1/offers/{id}/accept. The BR-1 self-offer
    //    guard it covered was NOT lost with it: the surviving V1 route carries the
    //    same check (JEBV4-83 F5, back-ported from this route precisely so the two
    //    could not diverge), and it is asserted by
    //    V1/JeebOffersAcceptHardeningTests.V1Accept_Genuine_Self_Offer_Returns_409_BR1_Before_Saga
    //    plus its negative twin V1Accept_By_Request_Owning_Client_Does_Not_Trip_BR1.
    //
    //    The DualRoleService-level BR-1 rule this class is really about is still
    //    covered directly by Same_Delivery_Violation_When_User_Is_Client above.
    // -----------------------------------------------------------------

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
            Phone = "+9613009999",
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
