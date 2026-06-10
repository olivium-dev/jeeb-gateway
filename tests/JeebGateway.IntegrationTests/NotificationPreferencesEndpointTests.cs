using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class NotificationPreferencesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private const string BaseRoute = "/api/users/me/notification-preferences";

    private readonly WebApplicationFactory<Program> _factory;

    public NotificationPreferencesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_Returns_Defaults_For_New_User()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-get-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.GetAsync(BaseRoute);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PrefsResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("user-get-1");
        body.Preferences.Offers.Should().BeTrue();
        body.Preferences.Chat.Should().BeTrue();
        body.Preferences.StatusChanges.Should().BeTrue();
        body.Preferences.RatingReminders.Should().BeTrue();
        body.Preferences.Promotions.Should().BeTrue();
        body.Preferences.Settlements.Should().BeTrue();
        body.AlwaysOn.Should().Contain(new[] { "otp", "system_critical", "kyc", "disputes" });
    }

    [Fact]
    public async Task Get_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync(BaseRoute);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_Updates_Only_Provided_Fields_And_Persists()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-2");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var patch = await client.PatchAsJsonAsync(BaseRoute, new { offers = false, chat = false });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPatch = await patch.Content.ReadFromJsonAsync<PrefsResponse>();
        afterPatch!.Preferences.Offers.Should().BeFalse();
        afterPatch.Preferences.Chat.Should().BeFalse();
        afterPatch.Preferences.StatusChanges.Should().BeTrue();
        afterPatch.Preferences.RatingReminders.Should().BeTrue();
        afterPatch.Preferences.Settlements.Should().BeTrue();

        var get = await client.GetAsync(BaseRoute);
        var afterGet = await get.Content.ReadFromJsonAsync<PrefsResponse>();
        afterGet!.Preferences.Offers.Should().BeFalse();
        afterGet.Preferences.Chat.Should().BeFalse();
    }

    [Fact]
    public async Task Patch_Updates_Settlements_Toggle()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-settle");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var patch = await client.PatchAsJsonAsync(BaseRoute, new { settlements = false });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await patch.Content.ReadFromJsonAsync<PrefsResponse>();
        body!.Preferences.Settlements.Should().BeFalse();
        body.Preferences.Offers.Should().BeTrue();
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_Otp()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-otp");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.PatchAsJsonAsync(BaseRoute, new { otp = false });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_SystemCritical()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-crit");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.PatchAsJsonAsync(BaseRoute, new { systemCritical = false });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_Kyc()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-kyc");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.PatchAsJsonAsync(BaseRoute, new { kyc = false });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_Disputes()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-disputes");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.PatchAsJsonAsync(BaseRoute, new { disputes = false });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Allows_Setting_Otp_To_True_As_No_Op()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-otp-true");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client");

        var resp = await client.PatchAsJsonAsync(BaseRoute, new { otp = true, ratingReminders = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PrefsResponse>();
        body!.Preferences.RatingReminders.Should().BeFalse();
        body.AlwaysOn.Should().Contain("otp");
    }

    [Fact]
    public async Task Preferences_Are_Isolated_Per_User()
    {
        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-User-Id", "user-iso-c");
        clientA.DefaultRequestHeaders.Add("X-User-Roles", "client");
        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-User-Id", "user-iso-d");
        clientB.DefaultRequestHeaders.Add("X-User-Roles", "client");

        await clientA.PatchAsJsonAsync(BaseRoute, new { offers = false });

        var bResp = await clientB.GetAsync(BaseRoute);
        var b = await bResp.Content.ReadFromJsonAsync<PrefsResponse>();
        b!.Preferences.Offers.Should().BeTrue();
    }

    private sealed record PrefsResponse(
        string UserId,
        Toggles Preferences,
        string[] AlwaysOn,
        DateTimeOffset UpdatedAt);

    private sealed record Toggles(
        bool Offers,
        bool Chat,
        bool StatusChanges,
        bool RatingReminders,
        bool Promotions,
        bool Settlements);
}
