using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using JeebGateway.service.ServiceChat;

namespace JeebGateway.Conversations;

/// <summary>
/// JEB-50 (S05 H7 / H9b): the <see cref="IConversationProvisioner"/> backed by
/// chat-service's existing <c>POST /api/channels</c> via the NSwag-generated
/// <see cref="ServiceChatClient"/>. Pure thin orchestration — it composes one
/// existing typed client call, tags the channel <c>broadcasting</c> (so
/// chat-service's <c>ChannelSummaryService.ResolvePhase</c> surfaces
/// <c>phase: "broadcasting"</c>), and returns the channel id. It holds no
/// conversation state and no domain logic: chat-service owns the conversation.
///
/// LIFETIME: the durable create path runs inside a singleton
/// <c>DurableRequestsStore</c>, while <see cref="ServiceChatClient"/> is a
/// SCOPED typed client. This provisioner therefore opens a fresh DI scope per
/// call and resolves the chat client from it — so no scoped/HttpClient instance
/// is captured for the app lifetime (avoiding the captive-dependency pitfall)
/// and each order's conversation create gets a fresh pooled handler.
///
/// DEGRADE-DON'T-FAIL: a chat-service blip (timeout, 5xx, null id) returns
/// <c>null</c> and the order create still succeeds — the conversation is a
/// secondary side-effect of create, not the matching-resolve hard dependency.
/// This mirrors <c>StateServiceSagaBundleRecorder</c>'s contract.
/// </summary>
public sealed class ChatServiceConversationProvisioner : IConversationProvisioner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ConversationProvisionOptions _options;
    private readonly ILogger<ChatServiceConversationProvisioner> _logger;

    public ChatServiceConversationProvisioner(
        IServiceScopeFactory scopeFactory,
        IOptions<ConversationProvisionOptions> options,
        ILogger<ChatServiceConversationProvisioner> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string?> CreateBroadcastingConversationAsync(
        string requestId,
        string clientId,
        CancellationToken ct)
    {
        if (!_options.Enabled) return null;

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var chat = scope.ServiceProvider.GetRequiredService<ServiceChatClient>();

            // S05 H9b ROOT-CAUSE FIX: chat-service's POST /api/channels
            // (ChannelService.CreateChannel) calls _memberRepository.GetByIdAsync
            // on the supplied MemberId and throws BadRequestException("Member
            // '...' does not exist") when no matching member row is present. The
            // user-management clientId is NOT a chat member id, so passing it
            // straight through made channel-create fail and conversationId
            // degraded to null even with ConversationAutoCreate ON.
            //
            // chat-service MINTS its own member id (MemberService.CreateAsync ->
            // response.ID = repo.AddAsync(member)); the client cannot supply it.
            // So we first register a chat member for this order's initiating
            // client via POST /api/members and use the RETURNED minted id as the
            // channel's MemberId. The member carries the clientId in its Name for
            // correlation/observability. Pure thin orchestration over two existing
            // typed-client calls — the gateway holds no member/conversation state.
            var memberResponse = await chat.MembersPOST2Async(new CreateMemberRequest
            {
                // Carry the user-management client id as the member name so the
                // chat member is correlatable back to the ordering client.
                Name = clientId,
                Nickname = clientId,
                Type = "client",
                Tag = _options.BroadcastingTag,
            }, ct);

            var memberId = memberResponse?.Id;
            if (string.IsNullOrWhiteSpace(memberId))
            {
                _logger.LogWarning(
                    "Conversation auto-create for order {RequestId} could not register a chat member for client {ClientId}; order persists without a conversation.",
                    requestId, clientId);
                return null;
            }

            var body = new CreateChannelRequest
            {
                // The minted chat member id (NOT the raw user-management clientId)
                // is the initiating member of the broadcasting conversation, so
                // chat-service's member-existence check passes. Candidate Jeebers
                // join later (S06 fan-out / accept).
                MemberId = memberId,
                Name = $"order-{requestId}",
                Description = "Order broadcasting conversation",
                // Tag AND Type both carry the broadcasting marker; chat-service's
                // ResolvePhase matches either, so the phase resolves even if one
                // marker is dropped by a future chat-service change.
                Tag = _options.BroadcastingTag,
                Type = _options.BroadcastingTag,
            };

            var response = await chat.ChannelsAsync(body, ct);
            var conversationId = response?.Id;

            if (string.IsNullOrWhiteSpace(conversationId))
            {
                _logger.LogWarning(
                    "Conversation auto-create for order {RequestId} returned no channel id; order persists without a conversation.",
                    requestId);
                return null;
            }

            return conversationId;
        }
        catch (Exception ex)
        {
            // A chat-service outage must not fail the order create. Degrade:
            // the order persists with no conversation id (H9b unsatisfied for
            // this one order) rather than cascading a 500 onto POST /requests.
            _logger.LogWarning(ex,
                "Conversation auto-create for order {RequestId} unavailable; order persists without a conversation.",
                requestId);
            return null;
        }
    }
}
