using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class RoutingProblemDetailsStatusCodePagesTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RoutingProblemDetailsStatusCodePagesTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UnmatchedPath_Returns404ProblemDetailsBody()
    {
        var client = AuthedClient();

        var resp = await client.GetAsync("/definitely-not-a-real-gateway-route");

        await AssertProblemDetailsAsync(resp, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task WrongVerbForExistingRoute_Returns405ProblemDetailsBody()
    {
        var client = AuthedClient();

        var resp = await client.PutAsync("/requests", JsonContent.Create(new { }));

        await AssertProblemDetailsAsync(resp, HttpStatusCode.MethodNotAllowed);
    }

    // The gateway's dev/OpenMode auth trusts X-User-* headers (same pattern as sibling
    // integration tests' JeeberClient/ClientActor). Without auth, ANY request 401s before
    // routing decides 404/405 — so authenticate to actually reach the routing-level 4xx.
    private HttpClient AuthedClient()
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("X-User-Id", "routing-test-user");
        c.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return c;
    }

    private static async Task AssertProblemDetailsAsync(HttpResponseMessage resp, HttpStatusCode statusCode)
    {
        resp.StatusCode.Should().Be(statusCode);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.Should().NotBeNullOrWhiteSpace();

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be((int)statusCode);
    }
}
