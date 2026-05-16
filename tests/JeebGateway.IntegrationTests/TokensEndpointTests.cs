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

    [Fact]
    public async Task Password_Change_Revokes_Every_Outstanding_Refresh_Token()
    {
        var sessionA = await Issue("u-pwchange");
        var sessionB = await Issue("u-pwchange");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "u-pwchange");

        var resp = await client.PostAsJsonAsync("/users/me/password", new
        {
            currentPassword = "old-password-1",
            newPassword = "new-password-2"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Refresh(sessionA.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(sessionB.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Phone_Change_Revokes_Every_Outstanding_Refresh_Token()
    {
        var sessionA = await Issue("u-phchange");
        var sessionB = await Issue("u-phchange");

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "u-phchange");

        var resp = await client.PostAsJsonAsync("/users/me/phone", new
        {
            newPhone = "+96550009999",
            otpCode = "123456"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Refresh(sessionA.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(sessionB.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Admin_Suspension_Revokes_Every_Outstanding_Refresh_Token()
    {
        Seed(new UserProfile
        {
            Id = "u-suspend",
            Phone = "+96550002222",
            Name = "Omar",
            Roles = new List<string> { "customer" },
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        var sessionA = await Issue("u-suspend");
        var sessionB = await Issue("u-suspend");

        var admin = _factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-User-Id", "admin-1");
        admin.DefaultRequestHeaders.Add("X-User-Roles", "admin");

        var resp = await admin.PatchAsJsonAsync("/admin/users/u-suspend/suspend", new { reason = "audit" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SuspendUserResponse>();
        body!.RevokedTokenCount.Should().BeGreaterOrEqualTo(2);

        (await Refresh(sessionA.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Refresh(sessionB.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Suspend_Without_Admin_Role_Returns_403()
    {
        var nonAdmin = _factory.CreateClient();
        nonAdmin.DefaultRequestHeaders.Add("X-User-Id", "u-nonadmin");

        var resp = await nonAdmin.PatchAsJsonAsync("/admin/users/anyone/suspend", new { reason = "audit" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ChangePassword_Without_Body_Returns_400()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "u-pw-bad");

        var resp = await client.PostAsJsonAsync<object?>("/users/me/password", null);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePhone_Rejects_Malformed_Phone()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "u-ph-bad");

        var resp = await client.PostAsJsonAsync("/users/me/phone", new
        {
            newPhone = "not-a-phone",
            otpCode = "123456"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

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
