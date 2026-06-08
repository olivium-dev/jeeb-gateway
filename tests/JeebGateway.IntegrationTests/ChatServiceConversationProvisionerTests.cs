using System.Collections.Concurrent;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.service.ServiceChat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-50 (S05 H7 / H9b): the chat-backed conversation provisioner.
///
/// <para>
/// When the ConversationAutoCreate flag is OFF it is a no-op — returns null
/// WITHOUT opening a scope or touching chat-service, so today's green create path
/// is byte-for-byte unchanged until the flag is flipped.
/// </para>
/// <para>
/// When ON, the H9b ROOT-CAUSE FIX is the member-then-channel ordering:
/// chat-service's POST /api/channels (ChannelService.CreateChannel) rejects a
/// MemberId that has no member row ("Member '...' does not exist"). The raw
/// user-management clientId is NOT a chat member id, so the provisioner must
/// FIRST register a chat member (POST /api/members) and use the MINTED member id
/// as the channel's MemberId. These tests drive a fake ServiceChatClient
/// (resolved from a real DI scope, exactly as production does) to lock that
/// ordering + id propagation, and the degrade-don't-fail contract.
/// </para>
/// </summary>
public sealed class ChatServiceConversationProvisionerTests
{
    [Fact]
    public async Task Disabled_flag_returns_null_without_resolving_a_scope()
    {
        // A scope factory that throws if used — proves the disabled path never
        // opens a scope or reaches the chat client.
        var provisioner = new ChatServiceConversationProvisioner(
            new ThrowingScopeFactory(),
            Options.Create(new ConversationProvisionOptions { Enabled = false }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);

        var result = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "req-1", clientId: "client-1", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Enabled_creates_member_first_then_channel_with_minted_member_id()
    {
        // chat-service MINTS the member id; the channel must reference THAT id, not
        // the raw clientId. The fake mints "minted-member-7" and asserts the
        // channel-create carried it as MemberId.
        var chat = new FakeChatClient
        {
            MemberIdToMint = "minted-member-7",
            ChannelIdToMint = "channel-42",
        };
        var provisioner = NewProvisioner(chat, enabled: true);

        var conversationId = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "order-99", clientId: "um-client-1", CancellationToken.None);

        // The returned conversation id is the channel id from chat-service.
        conversationId.Should().Be("channel-42");

        // Ordering: member create happened, then channel create.
        chat.Calls.Should().Equal("members", "channels");

        // The member was registered for THIS client (correlatable by name).
        chat.LastMemberRequest!.Name.Should().Be("um-client-1");

        // The channel referenced the MINTED member id (NOT the raw clientId) so
        // chat-service's member-existence check passes — the H9b fix.
        chat.LastChannelRequest!.MemberId.Should().Be("minted-member-7");
        chat.LastChannelRequest.MemberId.Should().NotBe("um-client-1");

        // The broadcasting markers are stamped so chat-service's ResolvePhase can
        // surface phase:"broadcasting" on the summary.
        chat.LastChannelRequest.Tag.Should().Be("broadcasting");
        chat.LastChannelRequest.Type.Should().Be("broadcasting");
        chat.LastChannelRequest.Name.Should().Be("order-order-99");
    }

    [Fact]
    public async Task Enabled_degrades_to_null_when_member_create_returns_no_id()
    {
        // chat-service returning no member id must NOT 500 the order create and
        // must NOT attempt the channel create with a null member id.
        var chat = new FakeChatClient
        {
            MemberIdToMint = null,
            ChannelIdToMint = "channel-x",
        };
        var provisioner = NewProvisioner(chat, enabled: true);

        var conversationId = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "order-1", clientId: "um-client-1", CancellationToken.None);

        conversationId.Should().BeNull();
        // No channel create attempted once the member could not be registered.
        chat.Calls.Should().Equal("members");
    }

    [Fact]
    public async Task Enabled_degrades_to_null_when_member_create_throws()
    {
        // A chat-service blip on member-create degrades to null (order still
        // persists) rather than cascading a 500 onto POST /requests.
        var chat = new FakeChatClient { ThrowOnMembers = true };
        var provisioner = NewProvisioner(chat, enabled: true);

        var conversationId = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "order-1", clientId: "um-client-1", CancellationToken.None);

        conversationId.Should().BeNull();
    }

    private static ChatServiceConversationProvisioner NewProvisioner(FakeChatClient chat, bool enabled)
    {
        // Build a real DI container so the provisioner resolves the (fake) chat
        // client from a real scope, exactly as it does in production.
        var services = new ServiceCollection();
        services.AddScoped<ServiceChatClient>(_ => chat);
        var sp = services.BuildServiceProvider();

        return new ChatServiceConversationProvisioner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationProvisionOptions { Enabled = enabled }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("disabled provisioner must not open a DI scope");
    }

    /// <summary>
    /// Fake <see cref="ServiceChatClient"/> overriding the two typed-client calls
    /// the provisioner uses. Records call ordering + the last request bodies so
    /// the member-then-channel ordering and minted-id propagation are asserted.
    /// </summary>
    private sealed class FakeChatClient : ServiceChatClient
    {
        public FakeChatClient() : base("http://chat.test/", new HttpClient()) { }

        public string? MemberIdToMint { get; set; }
        public string? ChannelIdToMint { get; set; }
        public bool ThrowOnMembers { get; set; }

        public ConcurrentQueue<string> CallsQueue { get; } = new();
        public IReadOnlyList<string> Calls => CallsQueue.ToArray();
        public CreateMemberRequest? LastMemberRequest { get; private set; }
        public CreateChannelRequest? LastChannelRequest { get; private set; }

        public override Task<IdentityResponse> MembersPOST2Async(
            CreateMemberRequest body, CancellationToken cancellationToken)
        {
            CallsQueue.Enqueue("members");
            LastMemberRequest = body;
            if (ThrowOnMembers)
            {
                throw new InvalidOperationException("chat-service member create blip");
            }
            return Task.FromResult(new IdentityResponse { Id = MemberIdToMint });
        }

        public override Task<IdentityResponse> ChannelsAsync(
            CreateChannelRequest body, CancellationToken cancellationToken)
        {
            CallsQueue.Enqueue("channels");
            LastChannelRequest = body;
            return Task.FromResult(new IdentityResponse { Id = ChannelIdToMint });
        }
    }
}
