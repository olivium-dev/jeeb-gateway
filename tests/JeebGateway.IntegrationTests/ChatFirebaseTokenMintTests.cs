using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using JeebGateway.Chat.Firebase;
using JeebGateway.Controllers;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>The public gateway is authenticated composition, never a local signer.</summary>
public sealed class ChatFirebaseTokenMintTests
{
    private const string Route = "/v1/chat/firebase-token";

    [Theory]
    [InlineData("/v1/chat/firebase-token")]
    [InlineData("/chat/firebase-token")]
    public async Task Claim_identity_is_proxied_and_body_query_header_cannot_redirect_it(string route)
    {
        var owner = new FakeIdentity();
        using var factory = NewFactory(owner);
        var client = BearerClient(factory, new() { new("sub", "email@example.test"), new("sid", "actor-1"), new("roles", "client") });
        client.DefaultRequestHeaders.Add("X-User-Id", "victim");
        var response = await client.PostAsJsonAsync(route + "?uid=victim", new { uid = "victim" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("actor-1", owner.LastUid);
        var body = await response.Content.ReadFromJsonAsync<FirebaseTokenResponse>();
        Assert.Equal("actor-1", body!.Uid);
        Assert.Equal("owner-only-token", body.Token);
        Assert.True(response.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task Unauthenticated_request_does_not_reach_owner()
    {
        var owner = new FakeIdentity();
        using var factory = NewFactory(owner);
        var response = await factory.CreateClient().PostAsJsonAsync(Route, new { uid = "victim" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(owner.LastUid);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Trusted_header_without_claim_subject_cannot_mint(string environment)
    {
        var owner = new FakeIdentity();
        using var factory = NewFactory(owner, environment);
        var client = BearerClient(factory, new() { new("roles", "client") });
        client.DefaultRequestHeaders.Add("X-User-Id", "victim");
        var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(owner.LastUid);
    }

    [Fact]
    public async Task Invalid_bearer_cannot_reach_owner()
    {
        var owner = new FakeIdentity();
        using var factory = NewFactory(owner);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-session");
        var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(owner.LastUid);
    }

    [Fact]
    public async Task Missing_chat_capability_cannot_reach_owner()
    {
        var owner = new FakeIdentity();
        using var factory = NewFactory(owner);
        var response = await BearerClient(factory, new() { new("sub", "actor") }).PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(owner.LastUid);
    }

    [Theory]
    [InlineData("different-uid")]
    [InlineData("empty-token")]
    [InlineData("expired")]
    [InlineData("excessive-lifetime")]
    [InlineData("unavailable")]
    public async Task Invalid_owner_response_fails_closed_without_local_fallback(string fault)
    {
        var owner = new FakeIdentity { Fault = fault };
        using var factory = NewFactory(owner);
        var client = BearerClient(factory, new() { new("sub", "actor-1"), new("roles", "client") });
        var response = await client.PostAsync(Route, null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("owner-only-token", body);
        Assert.DoesNotContain("victim", body);
        Assert.DoesNotContain("sensitive-upstream-body", body);
    }

    private static WebApplicationFactory<Program> NewFactory(FakeIdentity owner, string environment = "Testing")
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureServices(services => services.AddSingleton<IChatFirebaseIdentityClient>(owner));
        });

    private sealed class FakeIdentity : IChatFirebaseIdentityClient
    {
        public string? LastUid { get; private set; }
        public string? Fault { get; init; }
        public Task<FirebaseTokenResponse> MintAsync(string uid, CancellationToken ct)
        {
            LastUid = uid;
            if (Fault == "unavailable") throw new HttpRequestException("sensitive-upstream-body");
            return Task.FromResult(new FirebaseTokenResponse
            {
                Token = Fault == "empty-token" ? "" : "owner-only-token",
                Uid = Fault == "different-uid" ? "victim" : uid,
                ExpiresAt = Fault == "expired" ? DateTime.UtcNow.AddMinutes(-1) : DateTime.UtcNow.AddMinutes(30),
                ExpiresInSeconds = Fault == "excessive-lifetime" ? 7200 : 1800,
            });
        }
    }

    private static HttpClient BearerClient(
        WebApplicationFactory<Program> factory, List<Claim> claims)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        return client;
    }


}
