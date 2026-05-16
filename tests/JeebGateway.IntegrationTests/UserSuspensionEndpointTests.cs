using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Admin;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-030 acceptance criteria:
///   1. Suspended users get 403 on all Client/Jeeber actions.
///   2. Suspension reason stored and visible to user.
///   3. Unsuspend restores full access.
///   4. All admin actions logged with timestamp and admin id.
/// </summary>
public class UserSuspensionEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public UserSuspensionEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Suspend_Without_Admin_Role_Returns_403()
    {
        SeedUser("susp-no-admin");
        var notAdmin = ClientFor("susp-caller-not-admin");

        var resp = await notAdmin.PatchAsJsonAsync("/admin/users/susp-no-admin/suspend", new { reason = "fraud" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Suspend_Without_Body_Returns_400()
    {
        SeedUser("susp-no-body");
        var admin = AdminClient("admin-suspend-1");

        var resp = await admin.PatchAsJsonAsync("/admin/users/susp-no-body/suspend", new { reason = "" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Suspend_Unknown_User_Returns_404()
    {
        var admin = AdminClient("admin-suspend-2");

        var resp = await admin.PatchAsJsonAsync("/admin/users/does-not-exist/suspend", new { reason = "fraud" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspend_Stores_Reason_Marks_User_And_Visible_On_GetMe()
    {
        SeedUser("susp-visible");
        var admin = AdminClient("admin-suspend-3");

        var resp = await admin.PatchAsJsonAsync(
            "/admin/users/susp-visible/suspend",
            new { reason = "chargeback fraud" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SuspendUserResponse>();
        body!.IsSuspended.Should().BeTrue();
        body.Reason.Should().Be("chargeback fraud");
        body.SuspendedBy.Should().Be("admin-suspend-3");

        // AC #2: reason visible to user via GET /users/me.
        var asUser = ClientFor("susp-visible");
        var me = await asUser.GetFromJsonAsync<UserProfileResponse>("/users/me");
        me!.IsSuspended.Should().BeTrue();
        me.SuspensionReason.Should().Be("chargeback fraud");
        me.SuspendedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Suspended_User_Cannot_Create_Request_403()
    {
        SeedUser("susp-cannot-request");
        var admin = AdminClient("admin-suspend-4");
        var suspendResp = await admin.PatchAsJsonAsync(
            "/admin/users/susp-cannot-request/suspend",
            new { reason = "policy violation" });
        suspendResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // AC #1: suspended user is blocked from the Client action.
        var asUser = ClientFor("susp-cannot-request");
        var createResp = await asUser.PostAsJsonAsync("/requests", new
        {
            description = "should be blocked",
            pickupAddress = "x",
            dropoffAddress = "y"
        });

        createResp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await createResp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Account is suspended.");
        problem.Detail.Should().Be("policy violation");
    }

    [Fact]
    public async Task Unsuspend_Restores_Full_Access()
    {
        SeedUser("susp-restored");
        var admin = AdminClient("admin-suspend-5");

        await admin.PatchAsJsonAsync("/admin/users/susp-restored/suspend", new { reason = "investigation" });

        // While suspended → request rejected.
        var asUser = ClientFor("susp-restored");
        var beforeUnsuspend = await asUser.PostAsJsonAsync("/requests", new { description = "blocked" });
        beforeUnsuspend.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // AC #3: unsuspend lifts the block.
        var unsuspendResp = await admin.PatchAsync("/admin/users/susp-restored/unsuspend", content: null);
        unsuspendResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await unsuspendResp.Content.ReadFromJsonAsync<UnsuspendUserResponse>();
        body!.IsSuspended.Should().BeFalse();
        body.UnsuspendedBy.Should().Be("admin-suspend-5");

        // Same user can now create a request. T-backend-007 added tier +
        // structured locations as required fields on the create body.
        var afterUnsuspend = await asUser.PostAsJsonAsync("/requests", new
        {
            description = "now allowed",
            tierId = "flash",
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });
        afterUnsuspend.StatusCode.Should().Be(HttpStatusCode.Created);

        // GET /users/me no longer reports suspension.
        var me = await asUser.GetFromJsonAsync<UserProfileResponse>("/users/me");
        me!.IsSuspended.Should().BeFalse();
        me.SuspensionReason.Should().BeNull();
        me.SuspendedAt.Should().BeNull();
    }

    [Fact]
    public async Task Admin_Actions_Are_Audited_With_Timestamp_And_AdminId()
    {
        SeedUser("susp-audited");
        var admin = AdminClient("admin-audit-1");

        await admin.PatchAsJsonAsync("/admin/users/susp-audited/suspend", new { reason = "kyc mismatch" });
        await admin.PatchAsync("/admin/users/susp-audited/unsuspend", content: null);

        // AC #4: both admin actions appended with admin id + timestamp.
        var auditLog = _factory.Services.GetRequiredService<IAdminAuditLog>();
        var entries = await auditLog.ListForEntityAsync("user", "susp-audited", CancellationToken.None);

        entries.Should().HaveCountGreaterOrEqualTo(2);
        entries.Should().AllSatisfy(e =>
        {
            e.AdminUserId.Should().Be("admin-audit-1");
            e.CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        });
        entries.Select(e => e.Action).Should().Contain(new[] { "suspend_user", "unsuspend_user" });
    }

    [Fact]
    public async Task Unsuspending_Unknown_User_Returns_404()
    {
        var admin = AdminClient("admin-suspend-404");

        var resp = await admin.PatchAsync("/admin/users/does-not-exist/unsuspend", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Suspended_Jeeber_Cannot_Submit_Offer_403()
    {
        // Seed a Jeeber, suspend them, then confirm POST
        // /requests/{id}/offers is blocked with the suspension reason.
        // Covers AC #1 for the Jeeber side.
        SeedUser("susp-jeeber");
        var admin = AdminClient("admin-suspend-jeeber");
        var suspendResp = await admin.PatchAsJsonAsync(
            "/admin/users/susp-jeeber/suspend",
            new { reason = "offer abuse" });
        suspendResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Stage an existing pending request that a Jeeber could legally bid on.
        var clientId = $"client-{Guid.NewGuid()}";
        using var scope = _factory.Services.CreateScope();
        var requests = scope.ServiceProvider.GetRequiredService<IRequestsStore>();
        var pending = await requests.CreateAsync(new CreateRequestInput
        {
            ClientId = clientId,
            Description = "Pick up a package"
        }, default);

        var jeeber = JeeberClient("susp-jeeber");
        var resp = await jeeber.PostAsJsonAsync(
            $"/requests/{pending.Id}/offers",
            new { fee = 5m, etaMinutes = 20 });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Account is suspended.");
        problem.Detail.Should().Be("offer abuse");
    }

    [Fact]
    public async Task Get_Admin_Actions_Returns_Audit_Log_For_User()
    {
        // AC #4: GET /admin/users/{id}/actions returns the audit log
        // populated by every prior admin mutation on the user.
        SeedUser("susp-actions-1");
        var admin = AdminClient("admin-actions-1");

        await admin.PatchAsJsonAsync("/admin/users/susp-actions-1/suspend", new { reason = "test" });
        await admin.PatchAsync("/admin/users/susp-actions-1/unsuspend", content: null);

        var resp = await admin.GetAsync("/admin/users/susp-actions-1/actions");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminUserActionsResponse>();

        body!.UserId.Should().Be("susp-actions-1");
        body.Items.Should().HaveCountGreaterOrEqualTo(2);
        body.Items.Select(i => i.Action).Should().Contain(new[] { "suspend_user", "unsuspend_user" });
        body.Items.Should().AllSatisfy(i =>
        {
            i.AdminUserId.Should().Be("admin-actions-1");
            i.CreatedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        });
        // Newest-first ordering.
        body.Items.Should().BeInDescendingOrder(i => i.CreatedAt);
    }

    [Fact]
    public async Task Get_Admin_Actions_Unknown_User_Returns_404()
    {
        var admin = AdminClient("admin-actions-404");

        var resp = await admin.GetAsync("/admin/users/does-not-exist/actions");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_Admin_Actions_Without_Admin_Role_Returns_403()
    {
        SeedUser("susp-actions-no-admin");
        var notAdmin = ClientFor("susp-actions-not-admin");

        var resp = await notAdmin.GetAsync("/admin/users/susp-actions-no-admin/actions");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // -----------------------------------------------------------------
    // Helpers (mirrors UsersEndpointTests).
    // -----------------------------------------------------------------

    private void SeedUser(string id)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(new UserProfile
        {
            Id = id,
            Phone = "+96550000000",
            Name = "Suspendable User",
            Roles = new List<string> { "customer" },
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-1)
        });
    }

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Seeded users hold the Client (customer) role; mirror that on
        // the request so RequireRole(Client) on POST /requests passes
        // and the suspension filter is what we're actually testing.
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private HttpClient AdminClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private HttpClient JeeberClient(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Matches the role label used by RequestOffersEndpointTests; the
        // RequireRole(Jeeber) filter passes on the canonical string and the
        // suspension filter is what we're actually exercising.
        client.DefaultRequestHeaders.Add("X-User-Roles", "driver");
        return client;
    }
}
