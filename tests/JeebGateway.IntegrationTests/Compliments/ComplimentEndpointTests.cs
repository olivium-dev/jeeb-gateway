using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Generated.ComplimentService;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Compliments;

/// <summary>
/// Gap 3 — the thin compliment BFF (<c>/api/compliments/*</c>) over the shared
/// compliment-service, gated by <c>FeatureFlags:UseUpstream:Compliment</c>.
///
/// These tests exercise the BFF contract: while the flag is OFF the surface 503s
/// (net-new kill-switch); while ON it forwards to the typed
/// <see cref="IComplimentServiceClient"/> (stubbed) deriving the acting user from
/// the identity header; anonymous callers are 401. The upstream wire shape itself
/// (snake_case routes / DTOs) is locked by the pinned spec +
/// Services/Generated/ComplimentServiceClient.cs.
/// </summary>
public class ComplimentEndpointTests
{
    [Fact]
    public async Task Create_Flag_On_Forwards_Caller_As_Sender()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var client = ClientFor(factory, "sender-1");

        var resp = await client.PostAsJsonAsync("/api/compliments", new
        {
            recipientId = "recipient-9",
            message = "Great delivery, thank you!",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ComplimentResponse>();
        body!.PartnerId1.Should().Be("sender-1", "the sender is the authenticated caller, never the body");
        body.PartnerId2.Should().Be("recipient-9");
        body.Message.Should().Be("Great delivery, thank you!");

        // The controller passed the caller as partner_id_1 to the upstream client.
        stub.LastCreate.Should().NotBeNull();
        stub.LastCreate!.PartnerId1.Should().Be("sender-1");
        stub.LastCreate.PartnerId2.Should().Be("recipient-9");
    }

    [Fact]
    public async Task Connections_Flag_On_Forwards_Caller_Id()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var client = ClientFor(factory, "user-conn");

        var resp = await client.GetAsync("/api/compliments/connections");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ids = await resp.Content.ReadFromJsonAsync<List<string>>();
        ids.Should().Contain("peer-a");
        stub.LastConnectionsUserId.Should().Be("user-conn");
    }

    [Fact]
    public async Task Received_Flag_On_Forwards_Caller_Id()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var client = ClientFor(factory, "user-recv");

        var resp = await client.GetAsync("/api/compliments/received");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        stub.LastReceivedUserId.Should().Be("user-recv");
    }

    [Fact]
    public async Task Conversation_Flag_On_Maps_Caller_And_From()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var client = ClientFor(factory, "viewer-1");

        var resp = await client.GetAsync("/api/compliments/conversation?fromUserId=author-2");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ConversationResponse>();
        body!.UserId1.Should().Be("viewer-1");
        body.UserId2.Should().Be("author-2");
        stub.LastConversation.Should().Be(("viewer-1", "author-2"));
    }

    [Fact]
    public async Task Conversation_Flag_On_Missing_From_Returns_400()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var client = ClientFor(factory, "viewer-1");

        var resp = await client.GetAsync("/api/compliments/conversation");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_Anonymous_Returns_401()
    {
        var stub = new StubCompliments();
        using var factory = ComplimentEnabledFactory(stub);
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/compliments", new
        {
            recipientId = "recipient-9",
            message = "hi",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_Flag_Off_Returns_503_KillSwitch()
    {
        // Default factory — Compliment flag is off in the test (appsettings.json) env.
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "off-user");

        var resp = await client.PostAsJsonAsync("/api/compliments", new
        {
            recipientId = "r",
            message = "m",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Connections_Flag_Off_Returns_503_KillSwitch()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "off-user");

        var resp = await client.GetAsync("/api/compliments/connections");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> ComplimentEnabledFactory(IComplimentServiceClient stub) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Compliment", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IComplimentServiceClient>();
                services.AddSingleton(stub);
            });
        });

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private sealed class StubCompliments : IComplimentServiceClient
    {
        public ComplimentCreate? LastCreate { get; private set; }
        public string? LastConnectionsUserId { get; private set; }
        public string? LastReceivedUserId { get; private set; }
        public (string, string)? LastConversation { get; private set; }

        public Task<ComplimentResponse> CreateComplimentAsync(ComplimentCreate body, CancellationToken cancellationToken = default)
        {
            LastCreate = body;
            return Task.FromResult(new ComplimentResponse
            {
                PartnerId1 = body.PartnerId1,
                PartnerId2 = body.PartnerId2,
                Message = body.Message,
                CreatedAt = DateTimeOffset.UnixEpoch,
            });
        }

        public Task<IReadOnlyList<string>> GetUserConnectionsAsync(string userId, CancellationToken cancellationToken = default)
        {
            LastConnectionsUserId = userId;
            return Task.FromResult<IReadOnlyList<string>>(new List<string> { "peer-a", "peer-b" });
        }

        public Task<IReadOnlyList<string>> GetReceivedComplimentsAsync(string userId, CancellationToken cancellationToken = default)
        {
            LastReceivedUserId = userId;
            return Task.FromResult<IReadOnlyList<string>>(new List<string> { "sender-x" });
        }

        public Task<ConversationResponse> GetConversationAsync(string userId1, string userId2, CancellationToken cancellationToken = default)
        {
            LastConversation = (userId1, userId2);
            return Task.FromResult(new ConversationResponse
            {
                UserId1 = userId1,
                UserId2 = userId2,
                TotalMessages = 1,
                FirstMessageDate = DateTimeOffset.UnixEpoch,
                LastMessageDate = DateTimeOffset.UnixEpoch,
                Messages = new List<ConversationMessage>
                {
                    new() { Content = "nice work", Timestamp = DateTimeOffset.UnixEpoch },
                },
            });
        }
    }
}
