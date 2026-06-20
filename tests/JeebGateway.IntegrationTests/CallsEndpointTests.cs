using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class CallsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CallsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateSession_Accepts_Mobile_Minimal_Body_When_Masked_Calls_Are_Enabled()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("MaskedCalls:Enabled", "true"));
        var client = ParticipantClient(factory, "call-user-1");

        var resp = await client.PostAsJsonAsync("/api/calls/session", new
        {
            deliveryId = "dlv-call-1"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MaskedCallSessionDto>();
        body!.SessionId.Should().NotBeNullOrWhiteSpace();
        body.ProxyNumber.Should().NotBeNullOrWhiteSpace();
        body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task CreateSession_Rejects_Blank_DeliveryId()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("MaskedCalls:Enabled", "true"));
        var client = ParticipantClient(factory, "call-user-2");

        var resp = await client.PostAsJsonAsync("/api/calls/session", new
        {
            deliveryId = " "
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateSession_Without_Identity_Returns_401()
    {
        using var factory = _factory.WithWebHostBuilder(builder =>
            builder.UseSetting("MaskedCalls:Enabled", "true"));
        var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/calls/session", new
        {
            deliveryId = "dlv-call-unauth"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static HttpClient ParticipantClient(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "client,jeeber");
        return client;
    }

    private sealed record MaskedCallSessionDto(
        string SessionId,
        string ProxyNumber,
        DateTimeOffset ExpiresAt);
}
