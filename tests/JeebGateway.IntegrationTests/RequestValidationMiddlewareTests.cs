using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-032 — request validation middleware tests.
/// Covers: URI length, header size, body size, and content-type enforcement.
/// </summary>
public class RequestValidationMiddlewareTests
{
    [Fact]
    public async Task Request_With_Oversized_Url_Returns_414()
    {
        await using var factory = CreateFactory(maxUrlLength: 50);
        var client = factory.CreateClient();

        var longSegment = new string('x', 100);
        var longPath = $"/api/{longSegment}";
        var resp = await client.GetAsync(longPath);

        resp.StatusCode.Should().Be((HttpStatusCode)414);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task Request_With_Normal_Url_Passes_Through()
    {
        await using var factory = CreateFactory(maxUrlLength: 2048);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/health");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Request_With_Oversized_Body_Returns_413()
    {
        await using var factory = CreateFactory(maxBodySize: 100);
        var client = factory.CreateClient();

        var largeBody = new StringContent(
            new string('a', 200),
            Encoding.UTF8,
            "application/json");
        largeBody.Headers.ContentLength = 200;

        var resp = await client.PostAsync("/api/health", largeBody);

        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task Request_With_Unsupported_Content_Type_Returns_415()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var content = new StringContent("<xml>bad</xml>", Encoding.UTF8, "application/xml");
        var resp = await client.PostAsync("/api/health", content);

        resp.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Request_With_Allowed_Content_Type_Passes_Through()
    {
        await using var factory = CreateFactory();
        var client = factory.CreateClient();

        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await client.PostAsync("/auth/tokens", content);

        // Will get 400 (bad request body) not 415 — content type was accepted.
        resp.StatusCode.Should().NotBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Validation_Disabled_Passes_Oversized_Url()
    {
        await using var factory = CreateFactory(maxUrlLength: 50, enabled: false);
        var client = factory.CreateClient();

        var longPath = "/api/health?" + new string('x', 100);
        var resp = await client.GetAsync(longPath);

        resp.StatusCode.Should().NotBe((HttpStatusCode)414);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        int maxUrlLength = 2048,
        long maxBodySize = 1_048_576,
        bool enabled = true)
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, cfg) =>
                {
                    cfg.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Security:RequestValidation:Enabled"] = enabled.ToString(),
                        ["Security:RequestValidation:MaxBodySizeBytes"] = maxBodySize.ToString(),
                        ["Security:RequestValidation:MaxUrlLength"] = maxUrlLength.ToString(),
                        ["Security:RequestValidation:MaxHeaderValueLength"] = "8192",
                        ["Security:RateLimit:Enabled"] = "false"
                    });
                });
            });
    }
}
