using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-032 — API key authentication middleware tests.
/// Covers: missing key 401, invalid key 403, valid key 200, non-internal routes bypass.
/// </summary>
public class ApiKeyAuthenticationTests
{
    private const string ValidServiceName = "delivery-service";
    private const string ValidApiKey = "test-api-key-12345";

    [Fact]
    public async Task Internal_Route_Without_ApiKey_Returns_401()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/internal/health");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Internal_Route_With_Invalid_ApiKey_Returns_403()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");

        var resp = await client.GetAsync("/internal/health");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Internal_Route_With_Valid_ApiKey_Returns_200()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", ValidApiKey);

        var resp = await client.GetAsync("/internal/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_Route_Passes_Without_ApiKey_When_Enabled()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ApiKey_Disabled_Allows_Internal_Route_Without_Key()
    {
        await using var factory = CreateFactory(enabled: false);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/internal/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static WebApplicationFactory<Program> CreateFactory(bool enabled = true)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:ApiKey:Enabled"] = enabled.ToString(),
                        ["Security:ApiKey:HeaderName"] = "X-Api-Key",
                        [$"Security:ApiKey:ServiceKeys:{ValidServiceName}"] = ValidApiKey,
                        ["Security:RateLimit:Enabled"] = "false"
                    });
                });
            });
    }
}
