using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Endpoint tests for the additive active-role switch route (R2-ROLE-SWITCH):
/// <c>POST /v1/users/me/role/switch</c>.
///
/// Covers: success (200 + TokenPairResponse), invalid/unheld role (400), active
/// deliveries blocking the switch (409), and unresolved identity (401). Asserts
/// the response reuses the existing TokenPairResponse shape.
/// </summary>
public class RoleSwitchEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RoleSwitchEndpointTests(WebApplicationFactory<Program> factory)
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
    public async Task Switch_To_Held_Role_Returns_200_With_TokenPair()
    {
        var userId = $"switch-ok-{Guid.NewGuid()}";
        SeedUser(userId, active: Roles.Client, roles: new[] { Roles.Client, Roles.Jeeber });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);

        var resp = await client.PostAsJsonAsync(
            "/v1/users/me/role/switch", new { targetRole = Roles.Jeeber });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
        body.AccessTokenExpiresInSeconds.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Switch_To_Unheld_Role_Returns_400()
    {
        var userId = $"switch-unheld-{Guid.NewGuid()}";
        SeedUser(userId, active: Roles.Client, roles: new[] { Roles.Client });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);

        var resp = await client.PostAsJsonAsync(
            "/v1/users/me/role/switch", new { targetRole = Roles.Jeeber });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(400);
    }

    [Fact]
    public async Task Switch_With_Active_Client_Delivery_Returns_409()
    {
        var userId = $"switch-busy-{Guid.NewGuid()}";
        SeedUser(userId, active: Roles.Client, roles: new[] { Roles.Client, Roles.Jeeber });

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", $"{Roles.Client},{Roles.Jeeber}");

        // Create an active delivery request as the Client, so BR-1 blocks the switch.
        var created = await client.PostAsJsonAsync("/requests", new
        {
            description = "Active client delivery, blocks role switch.",
            tierId = "flash",
            pickupLocation = new { lat = 24.7, lng = 46.7 },
            dropoffLocation = new { lat = 24.6, lng = 46.7 }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await client.PostAsJsonAsync(
            "/v1/users/me/role/switch", new { targetRole = Roles.Jeeber });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be(409);
    }

    [Fact]
    public async Task Switch_Without_Identity_Returns_401()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync(
            "/v1/users/me/role/switch", new { targetRole = Roles.Jeeber });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private void SeedUser(string userId, string active, string[] roles)
    {
        var store = _factory.Services.GetRequiredService<InMemoryUsersStore>();
        store.Seed(new UserProfile
        {
            Id = userId,
            Phone = "+9665" + Random.Shared.Next(1000000, 9999999),
            Name = "Switch Test",
            Roles = roles.ToList(),
            ActiveRole = active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
    }
}
