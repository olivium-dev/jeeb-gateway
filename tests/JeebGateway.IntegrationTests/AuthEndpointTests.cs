using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-043 ticket-spec routes: POST /auth/refresh and
/// POST /auth/logout. The legacy /auth/tokens/* routes have their own
/// suite in <see cref="TokensEndpointTests"/>; this one covers only the
/// ticket-required URLs plus the cross-cutting revocation triggers
/// (suspension, credential change) when exercised through the new
/// endpoints.
/// </summary>
public class AuthEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AuthEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Security:RateLimit:Enabled"] = "false"
                });
            });
        });
    }

    [Fact]
    public async Task Auth_Refresh_Rotates_Tokens_And_Burns_Old_On_Reuse()
    {
        var first = await Issue("u-auth-refresh");

        var rotated = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        rotated.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await rotated.Content.ReadFromJsonAsync<TokenPairResponse>();
        second!.RefreshToken.Should().NotBe(first.RefreshToken);
        second.AccessToken.Should().NotBe(first.AccessToken);

        // Reusing the original refresh token after rotation must burn the chain.
        var replay = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var stale = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = second.RefreshToken });
        stale.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_Refresh_Issues_15_Minute_Access_Token_And_30_Day_Refresh()
    {
        var first = await Issue("u-auth-lifetime");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        resp.EnsureSuccessStatusCode();

        var pair = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();

        var accessLifetime = pair!.AccessTokenExpiresAt - DateTimeOffset.UtcNow;
        accessLifetime.TotalMinutes.Should().BeApproximately(15, 1);

        var refreshLifetime = pair.RefreshTokenExpiresAt - DateTimeOffset.UtcNow;
        refreshLifetime.TotalDays.Should().BeApproximately(30, 0.05);
    }

    [Fact]
    public async Task Auth_Refresh_With_Missing_Body_Returns_400()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync<object?>("/auth/refresh", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Auth_Refresh_With_Unknown_Token_Returns_401()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = "nope-not-a-real-token" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_Logout_Revokes_The_Current_Refresh_Token()
    {
        var pair = await Issue("u-auth-logout");

        var logout = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/logout", new { refreshToken = pair.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/refresh", new { refreshToken = pair.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Auth_Logout_Only_Revokes_The_Presented_Refresh_Token()
    {
        // Two parallel sessions for the same user — logout on one must NOT
        // sign the other out. Org-wide revocation is reserved for credential
        // change and suspension.
        var sessionA = await Issue("u-multi-session");
        var sessionB = await Issue("u-multi-session");

        var logout = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/logout", new { refreshToken = sessionA.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Refresh(sessionA.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var bRotated = await Refresh(sessionB.RefreshToken);
        bRotated.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Auth_Logout_With_Missing_Body_Returns_400()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync<object?>("/auth/logout", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // -----------------------------------------------------------------
    // Org-wide revocation TRIGGER tests removed with the user-management
    // literal replace. The triggers — PATCH /admin/users/{id}/suspend
    // (AdminUsersController) and POST /users/me/password (UsersController) —
    // no longer exist; user-management is proxied via
    // UserController/ServiceUserManagementClient. Single-session revocation
    // through /auth/logout and rotation through /auth/refresh remain covered
    // by the tests above.
    // -----------------------------------------------------------------

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private async Task<TokenPairResponse> Issue(string userId)
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens", new { userId });
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<TokenPairResponse>())!;
    }

    private Task<HttpResponseMessage> Refresh(string refreshToken) =>
        _factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refreshToken });
}
