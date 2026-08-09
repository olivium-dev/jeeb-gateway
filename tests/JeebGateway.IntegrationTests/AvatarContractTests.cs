using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
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
/// F5 avatar contract (DECISION rulings 1/3/4 + A1/A2). ProfilePic stores a BARE CDN object ref;
/// read paths project it through <see cref="AvatarUrlResolver"/> and the profile PUT refuses to let
/// a self-referential display URL overwrite the stored ref.
/// </summary>
public sealed class AvatarContractTests
{
    private const string PublicBaseUrl = "http://192.168.2.39:10090";
    private const string Ref = "profile_avatar/7dcb45dffd1e4acc9cc23996198f7f99.jpg";

    // ----- A1 resolver shape -----

    [Fact]
    public void Absolutize_ObjectRef_BuildsGatewayAvatarUrl_WithDeterministicToken()
    {
        var first = AvatarUrlResolver.Absolutize(Ref, "u-1", PublicBaseUrl);
        var second = AvatarUrlResolver.Absolutize(Ref, "u-1", PublicBaseUrl + "/");

        first.Should().Be($"{PublicBaseUrl}/api/users/u-1/avatar?v=7dcb45dffd1e");
        second.Should().Be(first, "the token is derived from the ref, never from the clock, and the base is trimmed");
    }

    [Fact]
    public void Absolutize_DifferentObject_ChangesTheVersionToken()
    {
        AvatarUrlResolver.Absolutize("profile_avatar/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa.jpg", "u-1", PublicBaseUrl)
            .Should().NotBe(AvatarUrlResolver.Absolutize(Ref, "u-1", PublicBaseUrl));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("old-avatar.png")]
    [InlineData("http://cdn.example.com/a.png")]
    [InlineData("http://192.168.2.39:10090/api/users/u-1/avatar?v=1754689825")]
    [InlineData("https://any.host/api/users/u-1/avatar")]
    public void Absolutize_DegradesEverythingElseToNull(string? stored)
    {
        AvatarUrlResolver.Absolutize(stored, "u-1", PublicBaseUrl).Should().BeNull();
    }

    [Fact]
    public void Absolutize_ExternalHttpsUrl_PassesThroughVerbatim()
    {
        AvatarUrlResolver.Absolutize("https://cdn.jeeb.app/a/nour.png", "u-1", PublicBaseUrl)
            .Should().Be("https://cdn.jeeb.app/a/nour.png");
    }

    [Theory]
    [InlineData("http://192.168.2.39:10090/api/users/u-1/avatar?v=1", true)]
    [InlineData("https://other.host/API/Users/abc/Avatar", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData(Ref, false)]
    [InlineData("https://cdn.jeeb.app/a/nour.png", false)]
    public void IsSelfReferentialAvatarUrl_MatchesOnPathShapeOnly(string? value, bool expected)
    {
        AvatarUrlResolver.IsSelfReferentialAvatarUrl(value).Should().Be(expected);
    }

    // ----- A2 PUT normalization -----

    [Theory]
    [InlineData("/api/User/profile")]
    [InlineData("/api/User/profile/update")]
    public async Task ProfileUpdate_SelfReferentialProfilePic_PreservesTheStoredRef(string route)
    {
        var um = new StatefulUmClient { ProfilePic = Ref };
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = ClientFor(factory, userId);

        // The display-name save round-trips /me's avatarUrl straight back into profilePic.
        var put = await http.PutAsJsonAsync(route, new
        {
            userId,
            username = "Renamed",
            profilePic = $"{PublicBaseUrl}/api/users/{userId}/avatar?v=7dcb45dffd1e",
        });

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        um.ProfilePic.Should().Be(Ref, "a rename must never overwrite the stored object ref with a display URL");

        var me = await http.GetAsync("/v1/users/me");
        (await me.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>())
            .GetProperty("avatarUrl").GetString()
            .Should().Be($"{PublicBaseUrl}/api/users/{userId}/avatar?v=7dcb45dffd1e");
    }

    [Theory]
    [InlineData("/api/User/profile")]
    [InlineData("/api/User/profile/update")]
    public async Task ProfileUpdate_EmptyStringClear_ForwardsVerbatim(string route)
    {
        var um = new StatefulUmClient { ProfilePic = Ref };
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = ClientFor(factory, userId);

        (await http.PutAsJsonAsync(route, new { userId, profilePic = "" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        um.ProfilePic.Should().BeEmpty("remove-photo sends '' and it is NOT self-referential");

        var me = await http.GetAsync("/v1/users/me");
        var body = await me.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        (!body.TryGetProperty("avatarUrl", out var avatar)
         || avatar.ValueKind == System.Text.Json.JsonValueKind.Null)
            .Should().BeTrue("a cleared avatar must not project any URL");
    }

    [Theory]
    [InlineData("/api/User/profile")]
    [InlineData("/api/User/profile/update")]
    public async Task ProfileUpdate_BareObjectRef_ForwardsVerbatim(string route)
    {
        var um = new StatefulUmClient();
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = ClientFor(factory, userId);

        (await http.PutAsJsonAsync(route, new { userId, profilePic = Ref }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        um.ProfilePic.Should().Be(Ref);
    }

    // ----- helpers (mirror ProfileUpdateCacheInvalidationTests) -----

    private static WebApplicationFactory<Program> MakeFactory(StatefulUmClient um) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:PublicBaseUrl", PublicBaseUrl);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
                services.Configure<UpstreamFeatureFlags>(f => f.UserManagement = true);
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, userId));
        return http;
    }

    private static string MintGatewayBearer(WebApplicationFactory<Program> factory, string userId)
    {
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var signingKey = config["Jwt:SigningKey"]!;

        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", userId),
            new(System.Security.Claims.ClaimTypes.Sid, userId),
            new("active_role", Roles.Client),
            new("roles", Roles.Client),
        };

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: config["Jwt:Issuer"]!,
            audience: config["Jwt:Audience"]!,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed class StatefulUmClient : UmClient
    {
        public StatefulUmClient() : base("http://localhost", new HttpClient()) { }

        public string? Username { get; set; }
        public string? ProfilePic { get; set; }

        public override Task<UmProfileResponse> ProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult(new UmProfileResponse
            {
                UserId = userId,
                Username = Username,
                ProfilePic = ProfilePic,
            });

        public override Task<UmUpdateResponse> UpdateAsync(UmUpdateRequest? body, CancellationToken ct)
        {
            if (body?.Username is not null) Username = body.Username;
            if (body?.ProfilePic is not null) ProfilePic = body.ProfilePic;
            return Task.FromResult(new UmUpdateResponse
            {
                UserId = body?.UserId,
                Username = Username,
                ProfilePic = ProfilePic,
            });
        }
    }
}
