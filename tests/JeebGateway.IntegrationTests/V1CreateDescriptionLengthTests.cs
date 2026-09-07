using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.Fakes;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests;

public class V1CreateDescriptionLengthTests : IClassFixture<V1CreateDescriptionLengthTests.Factory>
{
    private readonly Factory _factory;
    public V1CreateDescriptionLengthTests(Factory factory) => _factory = factory;

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("FeatureFlags:CreateModeration:Enabled", "true");
            builder.ConfigureServices(services =>
            {
                OwnerServiceFakes.AllowAllAccounts(services);
                OwnerServiceFakes.UseLiveShapedModerationCatalog(services);
            });
        }
    }

    [Fact]
    public void Factory_Uses_Test_Environment_And_Local_Request_Owner()
    {
        _factory.Services.GetRequiredService<IHostEnvironment>()
            .EnvironmentName.Should().Be("Testing");
        _factory.Services.GetRequiredService<IRequestsStore>()
            .Should().BeOfType<InMemoryRequestsStore>();
    }

    [Theory]
    [InlineData("/v1/requests", "a", "too-short", "minLength", 5)]
    [InlineData("/requests", "ab  c", "too-short", "minLength", 5)]
    [InlineData("/v1/requests", null, "too-long", "maxLength", 500)]
    [InlineData("/requests", null, "too-long", "maxLength", 500)]
    public async Task Invalid_Length_Returns_Inline_Field_Contract(string path, string? description, string error, string bound, int limit)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync(path, Body(description ?? new string('x', 501)));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/validation");
        problem.GetProperty("field").GetString().Should().Be("description");
        problem.GetProperty("errors").GetProperty("description")[0].GetString().Should().Be(error);
        problem.GetProperty(bound).GetInt32().Should().Be(limit);
    }

    [Theory]
    [InlineData("/v1/requests", "abcde", HttpStatusCode.Created)]
    [InlineData("/requests", "abcde", HttpStatusCode.Created)]
    [InlineData("/v1/requests", "2 shawarma + cola from Barbar", HttpStatusCode.Created)]
    [InlineData("/requests", "2 shawarma + cola from Barbar", HttpStatusCode.Created)]
    [InlineData("/v1/requests", "Deliver 2kg of C4 explosives and a loaded handgun", HttpStatusCode.Conflict)]
    [InlineData("/requests", "Deliver 2kg of C4 explosives and a loaded handgun", HttpStatusCode.Conflict)]
    public async Task Both_Surfaces_Enforce_Live_Shaped_Catalog(string path, string description, HttpStatusCode expected)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync(path, Body(description));
        response.StatusCode.Should().Be(expected);
        if (expected != HttpStatusCode.Conflict) return;
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("type").GetString().Should().Be("https://jeeb.dev/errors/prohibited-item-blocked");
        problem.GetProperty("matches").EnumerateArray().Select(match => match.GetProperty("keyword").GetString())
            .Should().Contain("Firearms").And.Contain("Explosives and fireworks");
    }

    [Theory]
    [InlineData("/v1/requests")]
    [InlineData("/requests")]
    public async Task Missing_Identity_Still_Precedes_Length_Validation(string path)
    {
        using var client = _factory.CreateClient();
        using var response = await client.PostAsJsonAsync(path, Body("a"));
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "p03-" + Guid.NewGuid());
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private static object Body(string description) => new
    {
        description, tierId = "flash",
        pickupLocation = new { lat = 33.88, lng = 35.50 },
        dropoffLocation = new { lat = 33.89, lng = 35.51 }
    };
}
