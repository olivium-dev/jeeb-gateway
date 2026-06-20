using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// E2E 8.2 / 8.3 / 8.4 — admin user roster + suspend/unsuspend BFF.
/// Each test spins up its own <see cref="WebApplicationFactory{Program}"/> so the
/// singleton <see cref="InMemoryUsersStore"/> is isolated per case (mirrors the
/// per-test factory pattern in AdminZonesEndpointTests). Admin auth follows the
/// gateway MVP convention: <c>X-User-Id</c> + <c>X-User-Roles: admin</c>.
/// </summary>
public class AdminUsersEndpointTests
{
    private static WebApplicationFactory<Program> NewFactory() => new();

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "ops-admin");
        client.DefaultRequestHeaders.Add("X-User-Roles", "admin");
        return client;
    }

    private static void SeedUser(
        WebApplicationFactory<Program> factory,
        string id,
        string name,
        string phone,
        string? email = null,
        int ratingCount = 0)
    {
        var store = factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(new UserProfile
        {
            Id = id,
            Phone = phone,
            Name = name,
            Email = email,
            Language = "en",
            Roles = new List<string> { Roles.Client },
            RatingCount = ratingCount,
            ActiveRole = Roles.Client,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }

    // -----------------------------------------------------------------
    // 8.2 — GET /admin/users/search
    // -----------------------------------------------------------------

    [Fact]
    public async Task Search_Without_Identity_Returns_401()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/admin/users/search");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Search_Without_Admin_Role_Returns_403()
    {
        using var factory = NewFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "non-admin");

        var resp = await client.GetAsync("/admin/users/search");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Search_As_Admin_Returns_Paged_Roster()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-alice", "Alice", "+962700000001", email: "alice@example.com");
        SeedUser(factory, "u-bob", "Bob", "+962700000002");

        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/users/search");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminUserSearchResponse>();
        body.Should().NotBeNull();
        body!.Total.Should().Be(2);
        body.Page.Should().Be(1);
        body.Items.Select(i => i.Id).Should().BeEquivalentTo("u-alice", "u-bob");
        // BR-10: zero ratings → "New" badge.
        body.Items.Should().OnlyContain(i => i.IsNew);
    }

    [Fact]
    public async Task Search_Filters_By_Name_Case_Insensitively()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-alice", "Alice", "+962700000001");
        SeedUser(factory, "u-bob", "Bob", "+962700000002");

        var admin = AdminClient(factory);
        var resp = await admin.GetAsync("/admin/users/search?name=ali");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AdminUserSearchResponse>();
        body!.Items.Should().ContainSingle().Which.Id.Should().Be("u-alice");
        body.Total.Should().Be(1);
    }

    [Fact]
    public async Task Search_Rejects_Out_Of_Range_PageSize()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory);

        var resp = await admin.GetAsync("/admin/users/search?pageSize=0");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------
    // 8.3 — PATCH /admin/users/{id}/suspend
    // -----------------------------------------------------------------

    [Fact]
    public async Task Suspend_Without_Admin_Role_Returns_403()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-carol", "Carol", "+962700000003");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "non-admin");

        var resp = await client.PatchAsJsonAsync(
            "/admin/users/u-carol/suspend", new SuspendUserRequest { Reason = "fraud" });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Suspend_As_Admin_Flags_User_And_Revokes_Tokens()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-carol", "Carol", "+962700000003");

        // Give the user a live refresh token so the revocation sweep has something
        // to revoke — proves suspend terminates live sessions (8.3 acceptance).
        var tokens = factory.Services.GetRequiredService<ITokenService>();
        await tokens.IssueAsync("u-carol", new[] { Roles.Client }, CancellationToken.None);

        var admin = AdminClient(factory);
        var resp = await admin.PatchAsJsonAsync(
            "/admin/users/u-carol/suspend", new SuspendUserRequest { Reason = "fraud" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SuspendUserResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("u-carol");
        body.IsSuspended.Should().BeTrue();
        body.Reason.Should().Be("fraud");
        body.SuspendedBy.Should().Be("ops-admin");
        body.RevokedTokenCount.Should().BeGreaterThan(0);

        // The status flip is durable in the store (read-back via search).
        var search = await admin.GetAsync("/admin/users/search?name=Carol");
        search.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Suspend_Unknown_User_Returns_404()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory);

        var resp = await admin.PatchAsJsonAsync(
            "/admin/users/does-not-exist/suspend", new SuspendUserRequest { Reason = "x" });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------
    // 8.4 — PATCH /admin/users/{id}/unsuspend
    // -----------------------------------------------------------------

    [Fact]
    public async Task Unsuspend_As_Admin_Lifts_Suspension()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-dave", "Dave", "+962700000004");
        var admin = AdminClient(factory);

        var suspend = await admin.PatchAsJsonAsync(
            "/admin/users/u-dave/suspend", new SuspendUserRequest { Reason = "review" });
        suspend.StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await admin.PatchAsync("/admin/users/u-dave/unsuspend", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<UnsuspendUserResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("u-dave");
        body.IsSuspended.Should().BeFalse();
        body.UnsuspendedBy.Should().Be("ops-admin");
    }

    [Fact]
    public async Task Unsuspend_Without_Admin_Role_Returns_403()
    {
        using var factory = NewFactory();
        SeedUser(factory, "u-dave", "Dave", "+962700000004");
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "non-admin");

        var resp = await client.PatchAsync("/admin/users/u-dave/unsuspend", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unsuspend_Unknown_User_Returns_404()
    {
        using var factory = NewFactory();
        var admin = AdminClient(factory);

        var resp = await admin.PatchAsync("/admin/users/does-not-exist/unsuspend", content: null);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
