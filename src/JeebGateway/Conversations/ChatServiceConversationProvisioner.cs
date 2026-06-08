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

        var body = new CreateChannelRequest
        {
            // The ordering client is the initiating member of the broadcasting
            // conversation. Candidate Jeebers join later (S06 fan-out / accept).
            MemberId = clientId,
            Name = $"order-{requestId}",
            Description = "Order broadcasting conversation",
            // Tag AND Type both carry the broadcasting marker; chat-service's
            // ResolvePhase matches either, so the phase resolves even if one
            // marker is dropped by a future chat-service change.
            Tag = _options.BroadcastingTag,
            Type = _options.BroadcastingTag,
        };

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var chat = scope.ServiceProvider.GetRequiredService<ServiceChatClient>();

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
