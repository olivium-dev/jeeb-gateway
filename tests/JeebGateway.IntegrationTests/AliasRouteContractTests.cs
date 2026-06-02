using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// R8-R9-UNVERSIONED-ALIASES — contract tests that pin the existing unversioned
/// token routes (<c>/auth/tokens</c>, <c>/auth/tokens/refresh</c>,
/// <c>/auth/tokens/revoke</c>) so they cannot be silently removed while mobile
/// clients still depend on them.
///
/// These are PURELY ADDITIVE tests. They do NOT add or modify any route. They
/// assert that the unversioned legacy surface keeps returning the existing
/// TokenPairResponse shape and status codes. If a future change deletes or
/// renames an unversioned route (before the documented ≥1 mobile-release-cycle
/// deprecation window has elapsed), these tests fail — gating the removal.
///
/// NOTE: no <c>/v1/auth/tokens/*</c> equivalents exist yet. When they are added,
/// extend this file to assert the old and new paths return identical shapes
/// (the GUARD intent) rather than removing the legacy assertions here.
/// </summary>
public class AliasRouteContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public AliasRouteContractTests(WebApplicationFactory<Program> factory)
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
    public async Task Legacy_AuthTokens_Issue_Route_Still_Mounted_With_TokenPair_Shape()
    {
        var http = _factory.CreateClient();

        var resp = await http.PostAsJsonAsync("/auth/tokens", new { userId = "alias-issue" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<TokenPairResponse>();
        body!.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();
        body.TokenType.Should().Be("Bearer");
    }

    [Fact]
    public async Task Legacy_AuthTokens_Refresh_Route_Still_Mounted_And_Rotates()
    {
        var http = _factory.CreateClient();

        var issued = await http.PostAsJsonAsync("/auth/tokens", new { userId = "alias-refresh" });
        var first = await issued.Content.ReadFromJsonAsync<TokenPairResponse>();

        var refreshed = await http.PostAsJsonAsync(
            "/auth/tokens/refresh", new { refreshToken = first!.RefreshToken });
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await refreshed.Content.ReadFromJsonAsync<TokenPairResponse>();
        second!.RefreshToken.Should().NotBe(first.RefreshToken);
    }

    [Fact]
    public async Task Legacy_AuthTokens_Revoke_Route_Still_Mounted_With_204()
    {
        var http = _factory.CreateClient();

        var issued = await http.PostAsJsonAsync("/auth/tokens", new { userId = "alias-revoke" });
        var pair = await issued.Content.ReadFromJsonAsync<TokenPairResponse>();

        var revoke = await http.PostAsJsonAsync(
            "/auth/tokens/revoke", new { refreshToken = pair!.RefreshToken });
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
