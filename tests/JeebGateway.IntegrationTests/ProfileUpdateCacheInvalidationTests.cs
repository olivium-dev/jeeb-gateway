using System.Net;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;
using UmUpdateRequest = JeebGateway.service.ServiceUserManagement.UpdateUserProfileRequest;
using UmUpdateResponse = JeebGateway.service.ServiceUserManagement.UpdateUserProfileResponse;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// F5 validator correction 2 (mandatory) — <c>GET /v1/users/me</c> caches the UM
/// profile for <c>ProfileCacheSeconds=30</c> and, before this fix, NEITHER
/// <c>PUT /api/User/profile</c> nor its twin <c>/api/User/profile/update</c> ever
/// invalidated that key. Pins that a profile write is immediately visible on the
/// very next <c>/me</c> read, not stale for up to 30s.
///
/// Also covers F5 validator correction 3 — <c>FirstNonBlank</c> already excludes
/// empty strings on both the upstream-echoed and submitted <c>ProfilePic</c>, so an
/// empty-string write can never clobber the local-projection mirror. Regression-only
/// (no new implementation): pinned on BOTH <c>/profile</c> and <c>/profile/update</c>
/// per the validator's note that the plan never mentioned the twin route.
/// </summary>
public sealed class ProfileUpdateCacheInvalidationTests
{
    [Theory]
    [InlineData("/api/User/profile")]
    [InlineData("/api/User/profile/update")]
    public async Task ProfileUpdate_InvalidatesUsersMeCache_SoNewAvatarIsVisibleImmediately(string route)
    {
        var um = new StatefulUmClient { ProfilePic = "old-avatar.png" };
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = ClientFor(factory, userId);

        // Prime the 30s /me cache with the OLD avatar.
        var before = await http.GetAsync("/v1/users/me");
        before.StatusCode.Should().Be(HttpStatusCode.OK);
        (await before.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("avatarUrl").GetString().Should().Be("old-avatar.png");

        var put = await http.PutAsJsonAsync(route, new { userId, profilePic = "new-avatar.png" });
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // Without the cache-invalidation fix this would still read the cached
        // "old-avatar.png" for up to ProfileCacheSeconds.
        var after = await http.GetAsync("/v1/users/me");
        after.StatusCode.Should().Be(HttpStatusCode.OK);
        (await after.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("avatarUrl").GetString().Should().Be("new-avatar.png",
                $"{route} must invalidate the /me profile cache, not just forward the write upstream");
    }

    [Theory]
    [InlineData("/api/User/profile")]
    [InlineData("/api/User/profile/update")]
    public async Task ProfileUpdate_EmptyStringProfilePic_DoesNotClobberLocalProjectionAvatar(string route)
    {
        var um = new StatefulUmClient();
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = ClientFor(factory, userId);

        // Establish a known-good local-projection avatar first.
        (await http.PutAsJsonAsync(route, new { userId, profilePic = "keep-me.png" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // A name-only-shaped save that (per the historical mobile defect) carries a
        // hardcoded empty-string profilePic must not wipe it.
        var resp = await http.PutAsJsonAsync(route, new { userId, username = "New Name", profilePic = "" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var store = factory.Services.GetRequiredService<IUsersStore>();
        var profile = await store.GetByIdAsync(userId, CancellationToken.None);
        profile!.AvatarUrl.Should().Be("keep-me.png",
            "FirstNonBlank must exclude an empty-string ProfilePic on both the echoed and submitted sides");
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> MakeFactory(StatefulUmClient um) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
                services.Configure<UpstreamFeatureFlags>(f => f.UserManagement = true);
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        // Opaque UM role "customer" == contract role "client"; ProfileReadSelf/
        // ProfileWriteSelf are granted to any-authenticated {client, jeeber, admin}.
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    /// <summary>
    /// Stub over the generated UM client that behaves like a real upstream: writes
    /// from <c>UpdateAsync</c> are visible to a subsequent <c>ProfileAsync</c> read —
    /// needed to prove the /me cache (not the upstream) is what's serving stale data.
    /// </summary>
    private sealed class StatefulUmClient : UmClient
    {
        public StatefulUmClient() : base("http://localhost", new HttpClient()) { }

        public string? Username { get; set; }
        public string? Email { get; set; }
        public string? ProfilePic { get; set; }

        public override Task<UmProfileResponse> ProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult(new UmProfileResponse
            {
                UserId = userId,
                Username = Username,
                Email = Email,
                ProfilePic = ProfilePic,
            });

        public override Task<UmUpdateResponse> UpdateAsync(UmUpdateRequest? body, CancellationToken ct)
        {
            if (body?.Username is not null) Username = body.Username;
            if (body?.Email is not null) Email = body.Email;
            if (body?.ProfilePic is not null) ProfilePic = body.ProfilePic;
            return Task.FromResult(new UmUpdateResponse
            {
                UserId = body?.UserId,
                Username = Username,
                Email = Email,
                ProfilePic = ProfilePic,
            });
        }
    }
}
