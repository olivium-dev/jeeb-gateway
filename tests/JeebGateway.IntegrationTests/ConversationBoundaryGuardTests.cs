using System.CodeDom.Compiler;
using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.service.ServiceChat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1475 — boundary regression guard for the request→conversation create path.
///
/// The corrections require (and the live code already satisfies) that the
/// request→conversation create is gateway-side orchestration over chat-service's
/// EXISTING generic channel-create + member-add, reached via the NSwag-generated
/// typed client — never a new product-namespaced shared endpoint and never a
/// hand-rolled HttpClient (correction #1 · GR2 · GR4). The Jeeb conversation
/// vocabulary (JeebConversationResponse, phase/role) stays in the gateway
/// (correction #3). These guards fail the build if any of those invariants
/// regress; they assert the boundary, not new behaviour.
/// </summary>
public sealed class ConversationBoundaryGuardTests
{
    [Fact]
    public void ServiceChatClient_Is_NSwag_Generated_Not_HandRolled()
    {
        // GR4: the gateway reaches the shared chat-service through the NSwag-
        // generated typed client, not a bespoke HttpClient.
        var generated = (GeneratedCodeAttribute?)Attribute.GetCustomAttribute(
            typeof(ServiceChatClient), typeof(GeneratedCodeAttribute));

        generated.Should().NotBeNull("ServiceChatClient must be the NSwag-generated typed client (GR4)");
        generated!.Tool.Should().Be("NSwag");
    }

    [Fact]
    public async Task Create_Orchestration_Composes_Generic_Member_Then_Channel_Primitives_Only()
    {
        // Correction #1 / GR2: create-or-get rides chat-service's EXISTING generic
        // primitives — member-create then channel-create — via the typed client.
        // No product-namespaced shared conversation endpoint is involved.
        var chat = new RecordingChatClient
        {
            MemberIdToMint = "minted-member-1",
            ChannelIdToMint = "channel-1",
        };

        var services = new ServiceCollection();
        services.AddScoped<ServiceChatClient>(_ => chat);
        using var sp = services.BuildServiceProvider();

        var provisioner = new ChatServiceConversationProvisioner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationProvisionOptions { Enabled = true }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);

        var conversationId = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "order-1", clientId: "client-1", CancellationToken.None);

        conversationId.Should().Be("channel-1");
        chat.Calls.Should().Equal("members", "channels");
    }

    [Fact]
    public void Product_Conversation_Vocabulary_Lives_In_The_Gateway_Not_Chat_Service()
    {
        // Correction #3: JeebConversationResponse + the phase/role vocabulary are
        // gateway-owned types, co-located with the orchestration — never part of the
        // shared chat-service contract.
        typeof(JeebConversationResponse).Assembly
            .Should().BeSameAs(typeof(ChatServiceConversationProvisioner).Assembly);
    }

    /// <summary>
    /// Fake NSwag <see cref="ServiceChatClient"/> recording the two generic
    /// primitive calls the create orchestration composes.
    /// </summary>
    private sealed class RecordingChatClient : ServiceChatClient
    {
        public RecordingChatClient() : base("http://chat.test/", new HttpClient()) { }

        public string? MemberIdToMint { get; set; }
        public string? ChannelIdToMint { get; set; }

        public ConcurrentQueue<string> CallsQueue { get; } = new();
        public IReadOnlyList<string> Calls => CallsQueue.ToArray();

        public override Task<IdentityResponse> MembersPOST2Async(
            CreateMemberRequest body, CancellationToken cancellationToken)
        {
            CallsQueue.Enqueue("members");
            return Task.FromResult(new IdentityResponse { Id = MemberIdToMint });
        }

        public override Task<IdentityResponse> ChannelsAsync(
            CreateChannelRequest body, CancellationToken cancellationToken)
        {
            CallsQueue.Enqueue("channels");
            return Task.FromResult(new IdentityResponse { Id = ChannelIdToMint });
        }
    }
}
