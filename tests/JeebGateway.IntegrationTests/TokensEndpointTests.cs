using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class TokensEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public TokensEndpointTests(WebApplicationFactory<Program> factory)
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
    public async Task Issue_Returns_AccessToken_With_15_Minute_Lifetime()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/auth/tokens", new { userId = "u-issue-1" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();

        var lifetime = body.AccessTokenExpiresAt - DateTimeOffset.UtcNow;
        lifetime.TotalMinutes.Should().BeApproximately(15, 1);

        var refreshLifetime = body.RefreshTokenExpiresAt - DateTimeOffset.UtcNow;
        refreshLifetime.TotalDays.Should().BeApproximately(30, 0.05);
    }

    [Fact]
    public async Task Issue_Embeds_Subject_And_Roles_In_Access_Token()
    {
        Seed(new UserProfile
        {
            Id = "u-claims",
            Phone = "+96550000111",
            Name = "Hala",
            Roles = new List<string> { "customer", "driver" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var pair = await Issue("u-claims");

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(pair.AccessToken);
        token.Subject.Should().Be("u-claims");
        token.Claims.Where(c => c.Type == "roles").Select(c => c.Value)
            .Should().BeEquivalentTo(new[] { "customer", "driver" });

        var minutesToExpiry = (token.ValidTo - DateTime.UtcNow).TotalMinutes;
        minutesToExpiry.Should().BeApproximately(15, 1);
    }

    [Fact]
    public async Task Access_Token_Validates_Under_Configured_Signing_Key()
    {
        var pair = await Issue("u-sigverify");

        var options = _factory.Services.GetRequiredService<IOptions<JwtOptions>>().Value;

        var handler = new JwtSecurityTokenHandler();
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(options.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(5)
        };

        var action = () => handler.ValidateToken(pair.AccessToken, parameters, out _);
        action.Should().NotThrow();
    }

    [Fact]
    public async Task Refresh_Rotates_The_Refresh_Token_And_Returns_A_New_Pair()
    {
        var first = await Issue("u-rotate");

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = first.RefreshToken });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();
        second!.RefreshToken.Should().NotBe(first.RefreshToken);
        second.AccessToken.Should().NotBe(first.AccessToken);
    }

    [Fact]
    public async Task Reusing_A_Rotated_Refresh_Token_Returns_401_And_Burns_The_Chain()
    {
        var first = await Issue("u-reuse");

        // First rotation succeeds.
        var rotated = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = first.RefreshToken });
        rotated.EnsureSuccessStatusCode();
        var second = await rotated.Content.ReadFromJsonAsync<TokenPairResponse>();

        // Replaying the OLD token is reuse → 401.
        var replay = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = first.RefreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // And the chain was burned, so the new token is also dead.
        var withNew = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = second!.RefreshToken });
        withNew.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_With_Unknown_Token_Returns_401()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = "totally-not-a-real-token" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Revoke_Endpoint_Invalidates_The_Refresh_Token()
    {
        var pair = await Issue("u-revoke");

        var revoke = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/revoke", new { refreshToken = pair.RefreshToken });
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var refresh = await _factory.CreateClient()
            .PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken = pair.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // -----------------------------------------------------------------
    // Token-revocation TRIGGER tests removed with the user-management literal
    // replace. The triggers — POST /users/me/password, POST /users/me/phone
    // (UsersController) and PATCH /admin/users/{id}/suspend (AdminUsers
    // controller) — no longer exist on the gateway; user-management is now
    // proxied through UserController/ServiceUserManagementClient. The core
    // token issue/refresh/rotate/revoke mechanism (TokenService /
    // /auth/tokens) is still exercised by the tests above.
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
        _factory.CreateClient().PostAsJsonAsync("/auth/tokens/refresh", new { refreshToken });

    private void Seed(UserProfile profile)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(profile);
    }
}
