using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class NotificationPreferencesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
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
        client.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        var resp = await client.GetAsync("/users/me/notification-preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PrefsResponse>();
        body.Should().NotBeNull();
        body!.UserId.Should().Be("user-get-1");
        body.Preferences.Offers.Should().BeTrue();
        body.Preferences.Chat.Should().BeTrue();
        body.Preferences.StatusChanges.Should().BeTrue();
        body.Preferences.RatingReminders.Should().BeTrue();
        body.AlwaysOn.Should().Contain(new[] { "otp", "system_critical" });
    }

    [Fact]
    public async Task Get_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/users/me/notification-preferences");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Patch_Updates_Only_Provided_Fields_And_Persists()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-1");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        var patch = await client.PatchAsJsonAsync(
            "/users/me/notification-preferences",
            new { offers = false, chat = false });

        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterPatch = await patch.Content.ReadFromJsonAsync<PrefsResponse>();
        afterPatch!.Preferences.Offers.Should().BeFalse();
        afterPatch.Preferences.Chat.Should().BeFalse();
        afterPatch.Preferences.StatusChanges.Should().BeTrue();
        afterPatch.Preferences.RatingReminders.Should().BeTrue();

        var get = await client.GetAsync("/users/me/notification-preferences");
        var afterGet = await get.Content.ReadFromJsonAsync<PrefsResponse>();
        afterGet!.Preferences.Offers.Should().BeFalse();
        afterGet.Preferences.Chat.Should().BeFalse();
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_Otp()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-otp");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        var resp = await client.PatchAsJsonAsync(
            "/users/me/notification-preferences",
            new { otp = false });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Rejects_Attempt_To_Disable_SystemCritical()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-crit");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        var resp = await client.PatchAsJsonAsync(
            "/users/me/notification-preferences",
            new { systemCritical = false });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_Allows_Setting_Otp_To_True_As_No_Op()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "user-patch-otp-true");
        client.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        var resp = await client.PatchAsJsonAsync(
            "/users/me/notification-preferences",
            new { otp = true, ratingReminders = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PrefsResponse>();
        body!.Preferences.RatingReminders.Should().BeFalse();
        body.AlwaysOn.Should().Contain("otp");
    }

    [Fact]
    public async Task Preferences_Are_Isolated_Per_User()
    {
        var clientA = _factory.CreateClient();
        clientA.DefaultRequestHeaders.Add("X-User-Id", "user-iso-a");
        clientA.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self
        var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("X-User-Id", "user-iso-b");
        clientB.DefaultRequestHeaders.Add("X-User-Roles", "client"); // ADR-005 §B notification.prefs.self

        await clientA.PatchAsJsonAsync(
            "/users/me/notification-preferences",
            new { offers = false });

        var bResp = await clientB.GetAsync("/users/me/notification-preferences");
        var b = await bResp.Content.ReadFromJsonAsync<PrefsResponse>();
        b!.Preferences.Offers.Should().BeTrue();
    }

    private sealed record PrefsResponse(
        string UserId,
        Toggles Preferences,
        string[] AlwaysOn,
        DateTimeOffset UpdatedAt);

    private sealed record Toggles(bool Offers, bool Chat, bool StatusChanges, bool RatingReminders);
}
